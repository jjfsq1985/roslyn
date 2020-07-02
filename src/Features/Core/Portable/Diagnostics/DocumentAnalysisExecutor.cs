﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.LanguageServices;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Workspaces.Diagnostics;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Diagnostics
{
    /// <summary>
    /// Executes analyzers on a document for computing local syntax/semantic diagnostics for a specific <see cref="DocumentAnalysisScope"/>.
    /// </summary>
    internal sealed class DocumentAnalysisExecutor
    {
        private readonly CompilationWithAnalyzers? _compilationWithAnalyzers;
        private readonly InProcOrRemoteHostAnalyzerRunner _diagnosticAnalyzerRunner;
        private readonly bool _logPerformanceInfo;

        private readonly ImmutableArray<DiagnosticAnalyzer> _compilationBasedAnalyzersInAnalysisScope;

        private ImmutableDictionary<DiagnosticAnalyzer, DiagnosticAnalysisResult>? _lazySyntaxDiagnostics;
        private ImmutableDictionary<DiagnosticAnalyzer, DiagnosticAnalysisResult>? _lazySemanticDiagnostics;

        public DocumentAnalysisExecutor(
            DocumentAnalysisScope analysisScope,
            CompilationWithAnalyzers? compilationWithAnalyzers,
            InProcOrRemoteHostAnalyzerRunner diagnosticAnalyzerRunner,
            bool logPerformanceInfo)
        {
            AnalysisScope = analysisScope;
            _compilationWithAnalyzers = compilationWithAnalyzers;
            _diagnosticAnalyzerRunner = diagnosticAnalyzerRunner;
            _logPerformanceInfo = logPerformanceInfo;

            var compilationBasedAnalyzers = compilationWithAnalyzers?.Analyzers.ToImmutableHashSet();
            _compilationBasedAnalyzersInAnalysisScope = compilationBasedAnalyzers != null
                ? analysisScope.Analyzers.WhereAsArray(compilationBasedAnalyzers.Contains)
                : ImmutableArray<DiagnosticAnalyzer>.Empty;
        }

        public DocumentAnalysisScope AnalysisScope { get; }

        /// <summary>
        /// Return all local diagnostics (syntax, semantic) that belong to given document for the given analyzer by calculating them.
        /// </summary>
        public async Task<IEnumerable<DiagnosticData>> ComputeDiagnosticsAsync(DiagnosticAnalyzer analyzer, CancellationToken cancellationToken)
        {
            Contract.ThrowIfFalse(AnalysisScope.Analyzers.Contains(analyzer));

            var document = AnalysisScope.Document;
            var span = AnalysisScope.Span;
            var kind = AnalysisScope.Kind;

            var loadDiagnostic = await document.State.GetLoadDiagnosticAsync(cancellationToken).ConfigureAwait(false);

            if (analyzer == FileContentLoadAnalyzer.Instance)
            {
                return loadDiagnostic != null ?
                    SpecializedCollections.SingletonEnumerable(DiagnosticData.Create(loadDiagnostic, document)) :
                    SpecializedCollections.EmptyEnumerable<DiagnosticData>();
            }

            if (loadDiagnostic != null)
            {
                return SpecializedCollections.EmptyEnumerable<DiagnosticData>();
            }

            if (analyzer is DocumentDiagnosticAnalyzer documentAnalyzer)
            {
                var documentDiagnostics = await AnalyzerHelper.ComputeDocumentDiagnosticAnalyzerDiagnosticsAsync(
                    documentAnalyzer, document, kind, _compilationWithAnalyzers?.Compilation, cancellationToken).ConfigureAwait(false);

                return documentDiagnostics.ConvertToLocalDiagnostics(document, span);
            }

            // quick optimization to reduce allocations.
            if (_compilationWithAnalyzers == null || !analyzer.SupportAnalysisKind(kind))
            {
                if (kind == AnalysisKind.Syntax)
                {
                    Logger.Log(FunctionId.Diagnostics_SyntaxDiagnostic,
                        (r, d, a, k) => $"Driver: {r != null}, {d.Id}, {d.Project.Id}, {a}, {k}", _compilationWithAnalyzers, document, analyzer, kind);
                }

                return SpecializedCollections.EmptyEnumerable<DiagnosticData>();
            }

            // if project is not loaded successfully then, we disable semantic errors for compiler analyzers
            var isCompilerAnalyzer = analyzer.IsCompilerAnalyzer();
            if (kind != AnalysisKind.Syntax && isCompilerAnalyzer)
            {
                var isEnabled = await document.Project.HasSuccessfullyLoadedAsync(cancellationToken).ConfigureAwait(false);

                Logger.Log(FunctionId.Diagnostics_SemanticDiagnostic, (a, d, e) => $"{a}, ({d.Id}, {d.Project.Id}), Enabled:{e}", analyzer, document, isEnabled);

                if (!isEnabled)
                {
                    return SpecializedCollections.EmptyEnumerable<DiagnosticData>();
                }
            }

            ImmutableArray<DiagnosticData> diagnostics;
            switch (kind)
            {
                case AnalysisKind.Syntax:
                    diagnostics = await GetSyntaxDiagnosticsAsync(analyzer, isCompilerAnalyzer, cancellationToken).ConfigureAwait(false);
                    break;

                case AnalysisKind.Semantic:
                    diagnostics = await GetSemanticDiagnosticsAsync(analyzer, isCompilerAnalyzer, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    throw ExceptionUtilities.UnexpectedValue(kind);
            }

#if DEBUG
            var diags = await diagnostics.ToDiagnosticsAsync(AnalysisScope.Document.Project, cancellationToken).ConfigureAwait(false);
            Debug.Assert(diags.Length == CompilationWithAnalyzers.GetEffectiveDiagnostics(diags, _compilationWithAnalyzers.Compilation).Count());
            Debug.Assert(diagnostics.Length == diags.ConvertToLocalDiagnostics(document, span).Count());
#endif

            return diagnostics;
        }

        private async Task<ImmutableDictionary<DiagnosticAnalyzer, DiagnosticAnalysisResult>> GetAnalysisResultAsync(DocumentAnalysisScope analysisScope, CancellationToken cancellationToken)
        {
            RoslynDebug.Assert(_compilationWithAnalyzers != null);

            var resultAndTelemetry = await _diagnosticAnalyzerRunner.AnalyzeDocumentAsync(analysisScope, _compilationWithAnalyzers,
                _logPerformanceInfo, getTelemetryInfo: false, cancellationToken).ConfigureAwait(false);
            return resultAndTelemetry.AnalysisResult;
        }

        private async Task<ImmutableArray<DiagnosticData>> GetCompilerAnalyzerDiagnosticsAsync(DiagnosticAnalyzer analyzer, TextSpan? span, CancellationToken cancellationToken)
        {
            RoslynDebug.Assert(analyzer.IsCompilerAnalyzer());
            RoslynDebug.Assert(_compilationWithAnalyzers != null);
            RoslynDebug.Assert(_compilationBasedAnalyzersInAnalysisScope.Contains(analyzer));

            var analysisScope = AnalysisScope.WithAnalyzers(ImmutableArray.Create(analyzer)).WithSpan(span);
            var analysisResult = await GetAnalysisResultAsync(analysisScope, cancellationToken).ConfigureAwait(false);
            if (!analysisResult.TryGetValue(analyzer, out var result))
            {
                return ImmutableArray<DiagnosticData>.Empty;
            }

            return result.GetDocumentDiagnostics(analysisScope.Document.Id, analysisScope.Kind);
        }

        private async Task<ImmutableArray<DiagnosticData>> GetSyntaxDiagnosticsAsync(DiagnosticAnalyzer analyzer, bool isCompilerAnalyzer, CancellationToken cancellationToken)
        {
            // PERF:
            //  1. Compute diagnostics for all analyzers with a single invocation into CompilationWithAnalyzers.
            //     This is critical for better analyzer execution performance.
            //  2. Ensure that the compiler analyzer is treated specially and does not block on diagnostic computation
            //     for rest of the analyzers. This is needed to ensure faster refresh for compiler diagnostics while typing.

            RoslynDebug.Assert(_compilationWithAnalyzers != null);
            RoslynDebug.Assert(_compilationBasedAnalyzersInAnalysisScope.Contains(analyzer));

            if (isCompilerAnalyzer)
            {
                return await GetCompilerAnalyzerDiagnosticsAsync(analyzer, AnalysisScope.Span, cancellationToken).ConfigureAwait(false);
            }

            if (_lazySyntaxDiagnostics == null)
            {
                var analysisScope = AnalysisScope.WithAnalyzers(_compilationBasedAnalyzersInAnalysisScope);
                var syntaxDiagnostics = await GetAnalysisResultAsync(analysisScope, cancellationToken).ConfigureAwait(false);
                Interlocked.CompareExchange(ref _lazySyntaxDiagnostics, syntaxDiagnostics, null);
            }

            return _lazySyntaxDiagnostics.TryGetValue(analyzer, out var diagnosticAnalysisResult) ?
                diagnosticAnalysisResult.GetDocumentDiagnostics(AnalysisScope.Document.Id, AnalysisScope.Kind) :
                ImmutableArray<DiagnosticData>.Empty;
        }

        private async Task<ImmutableArray<DiagnosticData>> GetSemanticDiagnosticsAsync(DiagnosticAnalyzer analyzer, bool isCompilerAnalyzer, CancellationToken cancellationToken)
        {
            // PERF:
            //  1. Compute diagnostics for all analyzers with a single invocation into CompilationWithAnalyzers.
            //     This is critical for better analyzer execution performance through re-use of bound node cache.
            //  2. Ensure that the compiler analyzer is treated specially and does not block on diagnostic computation
            //     for rest of the analyzers. This is needed to ensure faster refresh for compiler diagnostics while typing.

            RoslynDebug.Assert(_compilationWithAnalyzers != null);

            var span = AnalysisScope.Span;
            if (isCompilerAnalyzer)
            {
#if DEBUG
                await VerifySpanBasedCompilerDiagnosticsAsync().ConfigureAwait(false);
#endif

                var adjustedSpan = await GetAdjustedSpanForCompilerAnalyzerAsync().ConfigureAwait(false);
                return await GetCompilerAnalyzerDiagnosticsAsync(analyzer, adjustedSpan, cancellationToken).ConfigureAwait(false);
            }

            if (_lazySemanticDiagnostics == null)
            {
                var analysisScope = AnalysisScope.WithAnalyzers(_compilationBasedAnalyzersInAnalysisScope);
                var semanticDiagnostics = await GetAnalysisResultAsync(analysisScope, cancellationToken).ConfigureAwait(false);
                Interlocked.CompareExchange(ref _lazySemanticDiagnostics, semanticDiagnostics, null);
            }

            return _lazySemanticDiagnostics.TryGetValue(analyzer, out var diagnosticAnalysisResult) ?
                diagnosticAnalysisResult.GetDocumentDiagnostics(AnalysisScope.Document.Id, AnalysisScope.Kind) :
                ImmutableArray<DiagnosticData>.Empty;

            async Task<TextSpan?> GetAdjustedSpanForCompilerAnalyzerAsync()
            {
                // This method is to workaround a bug (https://github.com/dotnet/roslyn/issues/1557)
                // once that bug is fixed, we should be able to use given span as it is.

                Debug.Assert(isCompilerAnalyzer);

                if (!span.HasValue)
                {
                    return null;
                }

                var service = AnalysisScope.Document.GetRequiredLanguageService<ISyntaxFactsService>();
                var root = await AnalysisScope.Document.GetRequiredSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                var startNode = service.GetContainingMemberDeclaration(root, span.Value.Start);
                var endNode = service.GetContainingMemberDeclaration(root, span.Value.End);

                if (startNode == endNode)
                {
                    // use full member span
                    if (service.IsMethodLevelMember(startNode))
                    {
                        return startNode.FullSpan;
                    }

                    // use span as it is
                    return span;
                }

                var startSpan = service.IsMethodLevelMember(startNode) ? startNode.FullSpan : span.Value;
                var endSpan = service.IsMethodLevelMember(endNode) ? endNode.FullSpan : span.Value;

                return TextSpan.FromBounds(Math.Min(startSpan.Start, endSpan.Start), Math.Max(startSpan.End, endSpan.End));
            }

#if DEBUG
            async Task VerifySpanBasedCompilerDiagnosticsAsync()
            {
                if (!span.HasValue)
                {
                    return;
                }

                // make sure what we got from range is same as what we got from whole diagnostics
                var model = await AnalysisScope.Document.GetRequiredSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                var rangeDeclaractionDiagnostics = model.GetDeclarationDiagnostics(span.Value).ToArray();
                var rangeMethodBodyDiagnostics = model.GetMethodBodyDiagnostics(span.Value).ToArray();
                var rangeDiagnostics = rangeDeclaractionDiagnostics.Concat(rangeMethodBodyDiagnostics).Where(shouldInclude).ToArray();

                var wholeDeclarationDiagnostics = model.GetDeclarationDiagnostics().ToArray();
                var wholeMethodBodyDiagnostics = model.GetMethodBodyDiagnostics().ToArray();
                var wholeDiagnostics = wholeDeclarationDiagnostics.Concat(wholeMethodBodyDiagnostics).Where(shouldInclude).ToArray();

                if (!AnalyzerHelper.AreEquivalent(rangeDiagnostics, wholeDiagnostics))
                {
                    // otherwise, report non-fatal watson so that we can fix those cases
                    FatalError.ReportWithoutCrash(new Exception("Bug in GetDiagnostics"));

                    // make sure we hold onto these for debugging.
                    GC.KeepAlive(rangeDeclaractionDiagnostics);
                    GC.KeepAlive(rangeMethodBodyDiagnostics);
                    GC.KeepAlive(rangeDiagnostics);
                    GC.KeepAlive(wholeDeclarationDiagnostics);
                    GC.KeepAlive(wholeMethodBodyDiagnostics);
                    GC.KeepAlive(wholeDiagnostics);
                }

                return;

                static bool IsUnusedImportDiagnostic(Diagnostic d)
                {
                    switch (d.Id)
                    {
                        case "CS8019":
                        case "BC50000":
                        case "BC50001":
                            return true;
                        default:
                            return false;
                    }
                }

                // Exclude unused import diagnostics since they are never reported when a span is passed.
                // (See CSharp/VisualBasicCompilation.GetDiagnosticsForMethodBodiesInTree.)
                bool shouldInclude(Diagnostic d) => span.Value.IntersectsWith(d.Location.SourceSpan) && !IsUnusedImportDiagnostic(d);
            }
#endif
        }
    }
}
