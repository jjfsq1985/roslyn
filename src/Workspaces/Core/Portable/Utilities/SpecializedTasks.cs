﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Roslyn.Utilities
{
    internal static class SpecializedTasks
    {
        public static readonly Task<bool> True = Task.FromResult(true);
        public static readonly Task<bool> False = Task.FromResult(false);

        // This is being consumed through InternalsVisibleTo by Source-Based test discovery
        [Obsolete("Use Task.CompletedTask instead which is available in the framework.")]
        public static readonly Task EmptyTask = Task.CompletedTask;

#pragma warning disable VSTHRD200 // Use "Async" suffix for async methods
        public static Task<T?> AsNullable<T>(this Task<T> task) where T : class
#pragma warning restore VSTHRD200 // Use "Async" suffix for async methods
            => task!;

#pragma warning disable VSTHRD200 // Use "Async" suffix for async methods
        public static Task<T> Default<T>() where T : struct
#pragma warning restore VSTHRD200 // Use "Async" suffix for async methods
            => TasksOfStruct<T>.Default;

#pragma warning disable VSTHRD200 // Use "Async" suffix for async methods
        public static Task<T?> Null<T>() where T : class
#pragma warning restore VSTHRD200 // Use "Async" suffix for async methods
            => TasksOfClass<T>.Null;

#pragma warning disable VSTHRD200 // Use "Async" suffix for async methods
        public static Task<IReadOnlyList<T>> EmptyReadOnlyList<T>()
#pragma warning restore VSTHRD200 // Use "Async" suffix for async methods
            => EmptyTasks<T>.EmptyReadOnlyList;

#pragma warning disable VSTHRD200 // Use "Async" suffix for async methods
        public static Task<IList<T>> EmptyList<T>()
#pragma warning restore VSTHRD200 // Use "Async" suffix for async methods
            => EmptyTasks<T>.EmptyList;

#pragma warning disable VSTHRD200 // Use "Async" suffix for async methods
        public static Task<ImmutableArray<T>> EmptyImmutableArray<T>()
#pragma warning restore VSTHRD200 // Use "Async" suffix for async methods
            => EmptyTasks<T>.EmptyImmutableArray;

#pragma warning disable VSTHRD200 // Use "Async" suffix for async methods
        public static Task<IEnumerable<T>> EmptyEnumerable<T>()
#pragma warning restore VSTHRD200 // Use "Async" suffix for async methods
            => EmptyTasks<T>.EmptyEnumerable;

#pragma warning disable VSTHRD200 // Use "Async" suffix for async methods
        public static Task<T> FromResult<T>(T t) where T : class
#pragma warning restore VSTHRD200 // Use "Async" suffix for async methods
            => FromResultCache<T>.FromResult(t);

        private static class TasksOfStruct<T> where T : struct
        {
            public static readonly Task<T> Default = Task.FromResult<T>(default);
        }

        private static class TasksOfClass<T> where T : class
        {
            public static readonly Task<T?> Null = Task.FromResult<T?>(null);
        }

        private static class EmptyTasks<T>
        {
            public static readonly Task<IEnumerable<T>> EmptyEnumerable = Task.FromResult<IEnumerable<T>>(SpecializedCollections.EmptyEnumerable<T>());
            public static readonly Task<ImmutableArray<T>> EmptyImmutableArray = Task.FromResult(ImmutableArray<T>.Empty);
            public static readonly Task<IList<T>> EmptyList = Task.FromResult(SpecializedCollections.EmptyList<T>());
            public static readonly Task<IReadOnlyList<T>> EmptyReadOnlyList = Task.FromResult(SpecializedCollections.EmptyReadOnlyList<T>());
        }

        private static class FromResultCache<T> where T : class
        {
            private static readonly ConditionalWeakTable<T, Task<T>> s_fromResultCache = new ConditionalWeakTable<T, Task<T>>();
            private static readonly ConditionalWeakTable<T, Task<T>>.CreateValueCallback s_taskCreationCallback = Task.FromResult<T>;

#pragma warning disable VSTHRD200 // Use "Async" suffix for async methods
            public static Task<T> FromResult(T t)
#pragma warning restore VSTHRD200 // Use "Async" suffix for async methods
            {
                return s_fromResultCache.GetValue(t, s_taskCreationCallback);
            }
        }
    }
}
