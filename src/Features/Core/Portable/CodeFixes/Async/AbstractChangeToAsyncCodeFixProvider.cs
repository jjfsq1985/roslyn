﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;

namespace Microsoft.CodeAnalysis.CodeFixes.Async
{
    internal abstract partial class AbstractChangeToAsyncCodeFixProvider : AbstractAsyncCodeFix
    {
        public override FixAllProvider GetFixAllProvider()
        {
            // Fix All is not supported by this code fix
            // https://github.com/dotnet/roslyn/issues/34463
            return null;
        }

#pragma warning disable VSTHRD200 // Use "Async" suffix for async methods
        protected abstract Task<string> GetDescription(Diagnostic diagnostic, SyntaxNode node, SemanticModel semanticModel, CancellationToken cancellationToken);
#pragma warning restore VSTHRD200 // Use "Async" suffix for async methods
#pragma warning disable VSTHRD200 // Use "Async" suffix for async methods
        protected abstract Task<Tuple<SyntaxTree, SyntaxNode>> GetRootInOtherSyntaxTree(SyntaxNode node, SemanticModel semanticModel, Diagnostic diagnostic, CancellationToken cancellationToken);
#pragma warning restore VSTHRD200 // Use "Async" suffix for async methods

        protected override async Task<CodeAction> GetCodeActionAsync(
            SyntaxNode root, SyntaxNode node, Document document, Diagnostic diagnostic, CancellationToken cancellationToken)
        {
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);

            var result = await GetRootInOtherSyntaxTree(node, semanticModel, diagnostic, cancellationToken).ConfigureAwait(false);
            if (result == null)
            {
                return null;
            }

            var syntaxTree = result.Item1;
            var newRoot = result.Item2;
            var otherDocument = document.Project.Solution.GetDocument(syntaxTree);
            return new MyCodeAction(
                await GetDescription(diagnostic, node, semanticModel, cancellationToken).ConfigureAwait(false),
                token => Task.FromResult(otherDocument.WithSyntaxRoot(newRoot)));
        }

        private class MyCodeAction : CodeAction.DocumentChangeAction
        {
            public MyCodeAction(string title, Func<CancellationToken, Task<Document>> createChangedDocument)
                : base(title, createChangedDocument)
            {
            }
        }
    }
}
