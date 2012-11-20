using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Kudu.Contracts.Infrastructure
{
    public static class TaskExtensions
    {
        public static Task Then(this Task task, Action successor)
        {
            var tcs = new TaskCompletionSource<object>();

            if (task.Status == TaskStatus.RanToCompletion)
            {
                successor();
                tcs.SetResult(null);
                return tcs.Task;
            }
            else if (task.Status == TaskStatus.Faulted)
            {
                tcs.SetException(task.Exception);
                return tcs.Task;
            }

            task.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    tcs.SetException(t.Exception);
                }
                else if (t.IsCanceled)
                {
                    tcs.SetCanceled();
                }
                else
                {
                    try
                    {
                        successor();
                        tcs.SetResult(null);
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                }
            });

            return tcs.Task;
        }

        public static void Catch(this Task task, Action<Exception> handler)
        {
            task.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    Trace.TraceError(t.Exception.Message);
                    handler(t.Exception);
                }
            }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }

        public static Task Then<TInnerResult>(this Task<TInnerResult> task, Action<TInnerResult> continuation, CancellationToken cancellationToken = default(CancellationToken))
        {
            return task.ThenImpl(t => ToAsyncVoidTask(() => continuation(t.Result)), cancellationToken);
        }

        public static Task<TOuterResult> Then<TInnerResult, TOuterResult>(this Task<TInnerResult> task, Func<TInnerResult, TOuterResult> continuation, CancellationToken cancellationToken = default(CancellationToken))
        {
            return task.ThenImpl(t => FromResult(continuation(t.Result)), cancellationToken);
        }

        public static Task<TOuterResult> Then<TInnerResult, TOuterResult>(this Task<TInnerResult> task, Func<TInnerResult, Task<TOuterResult>> continuation, CancellationToken cancellationToken = default(CancellationToken))
        {
            return task.ThenImpl(t => continuation(t.Result), cancellationToken);
        }

        private static Task<TOuterResult> ThenImpl<TTask, TOuterResult>(this TTask task, Func<TTask, Task<TOuterResult>> continuation, CancellationToken cancellationToken)
            where TTask : Task
        {
            // Stay on the same thread if we can
            if (task.IsCanceled || cancellationToken.IsCancellationRequested)
            {
                return Canceled<TOuterResult>();
            }
            if (task.IsFaulted)
            {
                return FromErrors<TOuterResult>(task.Exception.InnerExceptions);
            }
            if (task.Status == TaskStatus.RanToCompletion)
            {
                try
                {
                    return continuation(task);
                }
                catch (Exception ex)
                {
                    return FromError<TOuterResult>(ex);
                }
            }

            SynchronizationContext syncContext = SynchronizationContext.Current;

            return task.ContinueWith(innerTask =>
            {
                if (innerTask.IsFaulted)
                {
                    return FromErrors<TOuterResult>(innerTask.Exception.InnerExceptions);
                }
                if (innerTask.IsCanceled)
                {
                    return Canceled<TOuterResult>();
                }

                TaskCompletionSource<Task<TOuterResult>> tcs = new TaskCompletionSource<Task<TOuterResult>>();
                if (syncContext != null)
                {
                    syncContext.Post(state =>
                    {
                        try
                        {
                            tcs.TrySetResult(continuation(task));
                        }
                        catch (Exception ex)
                        {
                            tcs.TrySetException(ex);
                        }
                    }, state: null);
                }
                else
                {
                    tcs.TrySetResult(continuation(task));
                }
                return tcs.Task.FastUnwrap();
            }, cancellationToken).FastUnwrap();
        }

        private static Task<TResult> FromResult<TResult>(TResult result)
        {
            TaskCompletionSource<TResult> tcs = new TaskCompletionSource<TResult>();
            tcs.SetResult(result);
            return tcs.Task;
        }

        private static Task<AsyncVoid> ToAsyncVoidTask(Action action)
        {
            return RunSynchronously<AsyncVoid>(() =>
            {
                action();
                return FromResult<AsyncVoid>(default(AsyncVoid));
            });
        }

        private static Task<TResult> FastUnwrap<TResult>(this Task<Task<TResult>> task)
        {
            Task<TResult> innerTask = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
            return innerTask ?? task.Unwrap();
        }

        private static Task<TResult> RunSynchronously<TResult>(Func<Task<TResult>> func, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Canceled<TResult>();
            }

            try
            {
                return func();
            }
            catch (Exception e)
            {
                return FromError<TResult>(e);
            }
        }

        public static Task FromError(Exception exception)
        {
            return FromError<AsyncVoid>(exception);
        }

        private static Task<TResult> FromError<TResult>(Exception exception)
        {
            TaskCompletionSource<TResult> tcs = new TaskCompletionSource<TResult>();
            tcs.SetException(exception);
            return tcs.Task;
        }

        private static Task<TResult> FromErrors<TResult>(IEnumerable<Exception> exceptions)
        {
            TaskCompletionSource<TResult> tcs = new TaskCompletionSource<TResult>();
            tcs.SetException(exceptions);
            return tcs.Task;
        }

        private static Task<TResult> Canceled<TResult>()
        {
            return CancelCache<TResult>.Canceled;
        }

        public static Task Catch(this Task task, Func<Exception, Task> continuation, CancellationToken cancellationToken = default(CancellationToken))
        {
            return task.CatchImpl(ex => continuation(ex).ToTask<AsyncVoid>(), cancellationToken);
        }

        private static Task<TResult> CatchImpl<TResult>(this Task task, Func<Exception, Task<TResult>> continuation, CancellationToken cancellationToken)
        {
            // Stay on the same thread if we can
            if (task.IsCanceled || cancellationToken.IsCancellationRequested)
            {
                return Canceled<TResult>();
            }
            if (task.IsFaulted)
            {
                try
                {
                    Task<TResult> resultTask = continuation(task.Exception.GetBaseException());
                    if (resultTask == null)
                    {
                        // Not a resource because this is an internal class, and this is a guard clause that's intended
                        // to be thrown by us to us, never escaping out to end users.
                        throw new InvalidOperationException("You cannot return null from the TaskHelpersExtensions.Catch continuation. You must return a valid task or throw an exception.");
                    }

                    return resultTask;
                }
                catch (Exception ex)
                {
                    return FromError<TResult>(ex);
                }
            }
            if (task.Status == TaskStatus.RanToCompletion)
            {
                TaskCompletionSource<TResult> tcs = new TaskCompletionSource<TResult>();
                tcs.TrySetFromTask(task);
                return tcs.Task;
            }

            return task.ContinueWith(innerTask =>
            {
                TaskCompletionSource<Task<TResult>> tcs = new TaskCompletionSource<Task<TResult>>();

                if (innerTask.IsFaulted)
                {
                    try
                    {
                        Task<TResult> resultTask = continuation(innerTask.Exception.GetBaseException());
                        if (resultTask == null)
                        {
                            throw new InvalidOperationException("You cannot return null from the TaskHelpersExtensions.Catch continuation. You must return a valid task or throw an exception.");
                        }

                        tcs.TrySetResult(resultTask);
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                }
                else
                {
                    tcs.TrySetFromTask(innerTask);
                }

                return tcs.Task.FastUnwrap();
            }, cancellationToken).FastUnwrap();
        }

        public static Task<TResult> ToTask<TResult>(this Task task, CancellationToken cancellationToken = default(CancellationToken), TResult result = default(TResult))
        {
            if (task == null)
            {
                return null;
            }

            // Stay on the same thread if we can
            if (task.IsCanceled || cancellationToken.IsCancellationRequested)
            {
                return Canceled<TResult>();
            }
            if (task.IsFaulted)
            {
                return FromErrors<TResult>(task.Exception.InnerExceptions);
            }
            if (task.Status == TaskStatus.RanToCompletion)
            {
                return FromResult(result);
            }

            return task.ContinueWith(innerTask =>
            {
                TaskCompletionSource<TResult> tcs = new TaskCompletionSource<TResult>();

                tcs.TrySetFromTask(innerTask, result);
                return tcs.Task;

            }, TaskContinuationOptions.ExecuteSynchronously).FastUnwrap();
        }

        public static bool TrySetFromTask<TResult>(this TaskCompletionSource<TResult> tcs, Task source, TResult result)
        {
            if (source.Status == TaskStatus.RanToCompletion)
            {
                return tcs.TrySetResult(result);
            }

            return tcs.TrySetFromTask(source);
        }

        public static bool TrySetFromTask<TResult>(this TaskCompletionSource<TResult> tcs, Task source)
        {
            if (source.Status == TaskStatus.Canceled)
            {
                return tcs.TrySetCanceled();
            }

            if (source.Status == TaskStatus.Faulted)
            {
                return tcs.TrySetException(source.Exception.InnerExceptions);
            }

            if (source.Status == TaskStatus.RanToCompletion)
            {
                Task<TResult> taskOfResult = source as Task<TResult>;
                return tcs.TrySetResult(taskOfResult == null ? default(TResult) : taskOfResult.Result);
            }

            return false;
        }

        public static bool TrySetFromTask<TResult>(this TaskCompletionSource<Task<TResult>> tcs, Task source)
        {
            if (source.Status == TaskStatus.Canceled)
            {
                return tcs.TrySetCanceled();
            }

            if (source.Status == TaskStatus.Faulted)
            {
                return tcs.TrySetException(source.Exception.InnerExceptions);
            }

            if (source.Status == TaskStatus.RanToCompletion)
            {
                // Sometimes the source task is Task<Task<TResult>>, and sometimes it's Task<TResult>.
                // The latter usually happens when we're in the middle of a sync-block postback where
                // the continuation is a function which returns Task<TResult> rather than just TResult,
                // but the originating task was itself just Task<TResult>. An example of this can be
                // found in TaskExtensions.CatchImpl().
                Task<Task<TResult>> taskOfTaskOfResult = source as Task<Task<TResult>>;
                if (taskOfTaskOfResult != null)
                {
                    return tcs.TrySetResult(taskOfTaskOfResult.Result);
                }

                Task<TResult> taskOfResult = source as Task<TResult>;
                if (taskOfResult != null)
                {
                    return tcs.TrySetResult(taskOfResult);
                }

                return tcs.TrySetResult(FromResult(default(TResult)));
            }

            return false;
        }

        public static Task Finally(this Task task, Action continuation)
        {
            return task.FinallyImpl<AsyncVoid>(continuation);
        }

        public static Task<TResult> Finally<TResult>(this Task<TResult> task, Action continuation)
        {
            return task.FinallyImpl<TResult>(continuation);
        }

        private static Task<TResult> FinallyImpl<TResult>(this Task task, Action continuation)
        {
            // Stay on the same thread if we can
            if (task.IsCompleted)
            {
                TaskCompletionSource<TResult> tcs = new TaskCompletionSource<TResult>();
                try
                {
                    continuation();
                    tcs.TrySetFromTask(task);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                return tcs.Task;
            }

            return task.ContinueWith(innerTask =>
            {
                TaskCompletionSource<TResult> tcs = new TaskCompletionSource<TResult>();

                try
                {
                    continuation();
                    tcs.TrySetFromTask(innerTask);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }

                return tcs.Task;
            }).FastUnwrap();
        }

        private struct AsyncVoid { }

        private static class CancelCache<TResult>
        {
            public static readonly Task<TResult> Canceled = GetCancelledTask();

            private static Task<TResult> GetCancelledTask()
            {
                TaskCompletionSource<TResult> tcs = new TaskCompletionSource<TResult>();
                tcs.SetCanceled();
                return tcs.Task;
            }
        }
    }
}
