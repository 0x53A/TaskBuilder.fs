﻿// TaskBuilder.fs - TPL task computation expressions for F#
//
// Written in 2016 by Robert Peele (humbobst@gmail.com)
//
// To the extent possible under law, the author(s) have dedicated all copyright and related and neighboring rights
// to this software to the public domain worldwide. This software is distributed without any warranty.
//
// You should have received a copy of the CC0 Public Domain Dedication along with this software.
// If not, see <http://creativecommons.org/publicdomain/zero/1.0/>.

namespace FSharp.Control.Tasks
open System
open System.Threading.Tasks
open System.Runtime.CompilerServices

// This module is not really obsolete, but it's not intended to be referenced directly from user code.
// However, it can't be private because it is used within inline functions that *are* user-visible.
// Marking it as obsolete is a workaround to hide it from auto-completion tools.
[<Obsolete>]
module TaskBuilder =
    /// Represents the state of a computation:
    /// either awaiting something with a continuation,
    /// or completed with a return value.
    /// The 'a generic parameter is the result type of this step, whereas the 'm generic parameter
    /// is the result type of the entire `task` block it occurs in.
    [<Struct>]
    type Step<'a, 'm> =
        /// If this task has produced a return value, this is that value, and the `Continuation`
        /// property will be null. Idiomatic F# would use a discriminated union but we want to
        /// avoid unnecessary allocations.
        val public ImmediateValue : 'a
        /// If non-null, an object implementing the next step in the task.
        val public Continuation : StepContinuation<'a, 'm>
        new(immediate, continuation) = { ImmediateValue = immediate; Continuation = continuation }
        /// Create a step from an immediately available return value.
        static member OfImmediate(immediate) = Step(immediate, Unchecked.defaultof<_>)
        /// Create a step from a continuation.
        static member OfContinuation(con) = Step(Unchecked.defaultof<_>, con)
    and
        /// Encapsulates the pairing of an awaitable and continuation that should execute
        /// when the awaitable has completed in order to reach the next step in the computation.
        [<AllowNullLiteral>]
        StepContinuation<'a, 'm> =
            /// A function which, given our state machine, might await the awaitable.
            /// Can return true to indicate that no await was actually performed and computation
            /// can proceed immediately to the next step.
            val public Await : StepStateMachine<'m> -> bool
            /// The delayed continuation which proceeds to the next step.
            /// Must not be called until the awaitable has finished.
            val public NextStep : unit -> Step<'a, 'm>
            new(await, nextStep) = { Await = await; NextStep = nextStep }
    /// Implements the machinery of running a `Step<'m, 'm>` as a `Task<'m>`.
    and StepStateMachine<'m>(step : Step<'m, 'm>) =
        let mutable methodBuilder = AsyncTaskMethodBuilder<'m>()
        let mutable step = step
        let mutable continuation = null : StepContinuation<_, _>

        /// Start execution as a `Task<'m>`.
        member this.Run() =
            let mutable this = this
            methodBuilder.Start(&this)
            methodBuilder.Task

        /// Tell the state machine to `MoveNext()` whenever the awaitable finishes.
        /// Always returns false (for convenience in implementing `StepContinuation.Await`).
        member this.Await(awaitable : 'await when 'await :> INotifyCompletion) =
            // Have to declare mutables so we can pass by reference.
            // We don't really need to keep the mutated versions of these though,
            // at least for the set of awaitable types we support.
            let mutable this = this
            let mutable awaiter = awaitable
            // Tell it to call our MoveNext() when this thing is done.
            methodBuilder.AwaitOnCompleted(&awaiter, &this)
            // Return false to indicate that we're awaiting something and can't proceed synchronously.
            false

        /// Return true if there was a pending continuation which, when ran,
        /// placed our methodBuilder into a faulted state.
        member inline private this.ContinuationFaults() =
            let currentContinuation = continuation
            if not (isNull currentContinuation) then
                continuation <- null
                try
                    step <- currentContinuation.NextStep()
                    false
                with
                | exn ->
                    methodBuilder.SetException(exn)
                    true
            else
                false
        /// Proceed to one of three states: result, failure, or awaiting.
        /// If awaiting, we can assume MoveNext() will be called again when the awaitable completes.
        member this.MoveNext() =
            // Don't do anything if running a pending continuation leads to a faulted state.
            if this.ContinuationFaults() then () else
            let stepContinuation = step.Continuation
            if isNull stepContinuation then // We must have a result.
                methodBuilder.SetResult(step.ImmediateValue)
            else // Time to await.
                continuation <- stepContinuation // Set the pending continuation for when we resume.
                if
                    try
                        // We decide whether to proceed based on the result of this call.
                        // The StepContinuation is in charge of figuring out if the awaitable is already complete.
                        stepContinuation.Await(this)
                    with
                    | exn ->
                        methodBuilder.SetException(exn)
                        false // Definitely shouldn't proceed if we're in a faulted state.
                then
                    // If we can proceed synchronously, then it's our responsibility to do so.
                    this.MoveNext()
               
        interface IAsyncStateMachine with
            member this.MoveNext() = this.MoveNext()
            member this.SetStateMachine(_) = () // Doesn't really apply since we're a reference type.

    /// Used to represent no-ops like the implicit empty "else" branch of an "if" expression.
    /// Notice that this doesn't impose any constraints on the return type of the task block.
    let zero() = Step<unit, _>.OfImmediate(())

    /// Used to return a value. Notice that the result type of this step must be the same as the
    /// result type of the entire method.
    let ret (x : 'a) = Step<'a, 'a>.OfImmediate(x)

    // The following various flavors of `bind` are for sequencing tasks with the continuations
    // that should run following them. They all follow pretty much the same formula.

    let bindTask (task : 'a Task) (continuation : 'a -> Step<'b, 'm>) =
        let taskAwaiter = task.GetAwaiter()
        StepContinuation
            ( (fun state -> if taskAwaiter.IsCompleted then true else state.Await(taskAwaiter))
            , (fun () -> continuation <| taskAwaiter.GetResult())
            ) |> Step<'b, 'm>.OfContinuation

    let bindVoidTask (task : Task) (continuation : unit -> Step<'b, 'm>) =
        StepContinuation
            ( (fun state ->
                let taskAwaiter = task.GetAwaiter()
                if taskAwaiter.IsCompleted then true else state.Await(taskAwaiter))
            , continuation
            ) |> Step<'b, 'm>.OfContinuation

    let bindConfiguredTask (task : 'a ConfiguredTaskAwaitable) (continuation : 'a -> Step<'b, 'm>) =
        let taskAwaiter = task.GetAwaiter()
        StepContinuation
            ( (fun state -> if taskAwaiter.IsCompleted then true else state.Await(taskAwaiter))
            , (fun () -> continuation <| taskAwaiter.GetResult())
            ) |> Step<'b, 'm>.OfContinuation

    let bindVoidConfiguredTask (task : ConfiguredTaskAwaitable) (continuation : unit -> Step<'b, 'm>) =
        StepContinuation
            ( (fun state ->
                let taskAwaiter = task.GetAwaiter()
                if taskAwaiter.IsCompleted then true else state.Await(taskAwaiter))
            , continuation
            ) |> Step<'b, 'm>.OfContinuation

    let inline
        bindGenericAwaitable< ^a, ^b, ^c, ^m when ^a : (member GetAwaiter : unit -> ^b) and ^b :> INotifyCompletion >
        (awt : ^a) (continuation : unit -> Step< ^c, ^m >) =
        let taskAwaiter = (^a : (member GetAwaiter : unit -> ^b)(awt))
        StepContinuation
            ( (fun state -> state.Await(taskAwaiter))
            , continuation
            ) |> Step< ^c, ^m >.OfContinuation

    let rec combine (step : Step<'a, 'm>) (continuation : unit -> Step<'b, 'm>) =
        let stepContinuation = step.Continuation
        if isNull stepContinuation then
            continuation()
        else
            let stepNext = stepContinuation.NextStep
            StepContinuation
                ( stepContinuation.Await
                , fun () -> combine (stepNext()) continuation
                ) |> Step<'b, 'm>.OfContinuation

    let rec whileLoop (cond : unit -> bool) (body : unit -> Step<unit, 'm>) =
        if cond() then combine (body()) (fun () -> whileLoop cond body)
        else zero()

    let rec tryWithCore (stepContinuation : StepContinuation<'a, 'm>) (catch : exn -> Step<'a, 'm>) =
        let stepNext = stepContinuation.NextStep
        StepContinuation
            ( stepContinuation.Await
            , fun () -> tryWithNonInline stepNext catch
            ) |> Step<'a, 'm>.OfContinuation

    and inline tryWith (step : unit -> Step<'a, 'm>) (catch : exn -> Step<'a, 'm>) =
        try
            let step = step()
            if isNull step.Continuation then
                step
            else
                tryWithCore step.Continuation catch
        with
        | exn -> catch exn

    and tryWithNonInline step catch = tryWith step catch

    let rec tryFinallyCore (stepContinuation : StepContinuation<'a, 'm>) (fin : unit -> unit) =
        let stepNext = stepContinuation.NextStep
        StepContinuation
            ( stepContinuation.Await
            , fun () -> tryFinallyNonInline stepNext fin
            ) |> Step<'a, 'm>.OfContinuation

    and inline tryFinally (step : unit -> Step<'a, 'm>) fin =
        try
            let step = step()
            if isNull step.Continuation then
                fin()
                step
            else
                tryFinallyCore step.Continuation fin
        with
        | _ ->
            fin()
            reraise()

    and tryFinallyNonInline step fin = tryFinally step fin

    let inline using (disp : #IDisposable) (body : _ -> Step<'a, 'm>) =
        tryFinally (fun () -> body disp) disp.Dispose

    let forLoop (sequence : 'a seq) (body : 'a -> Step<unit, 'm>) =
        using (sequence.GetEnumerator())
            (fun e -> whileLoop e.MoveNext (fun () -> body e.Current))

    let inline run (firstStep : unit -> Step<'m, 'm>) =
        try
            let step = firstStep()
            if isNull step.Continuation then
                Task.FromResult(step.ImmediateValue)
            else
                StepStateMachine<'m>(step).Run()
        with
        | exn -> Task.FromException<_>(exn)

    type TaskBuilder() =
        // These methods are consistent between the two builders.
        // Unfortunately, inline members do not work with inheritance.
        member inline __.Delay(f : unit -> Step<_, _>) = f
        member inline __.Run(f : unit -> Step<'m, 'm>) = run f
        member inline __.Zero() = zero()
        member inline __.Return(x) = ret x
        member inline __.ReturnFrom(task) = bindConfiguredTask task ret
        member inline __.ReturnFrom(task) = bindVoidConfiguredTask task ret
        member inline __.ReturnFrom(yld : YieldAwaitable) = bindGenericAwaitable yld ret
        member inline __.Combine(step, continuation) = combine step continuation
        member inline __.Bind(task, continuation) = bindConfiguredTask task continuation
        member inline __.Bind(task, continuation) = bindVoidConfiguredTask task continuation
        member inline __.Bind(yld : YieldAwaitable, continuation) = bindGenericAwaitable yld continuation
        member inline __.While(condition, body) = whileLoop condition body
        member inline __.For(sequence, body) = forLoop sequence body
        member inline __.TryWith(body, catch) = tryWith body catch
        member inline __.TryFinally(body, fin) = tryFinally body fin
        member inline __.Using(disp, body) = using disp body
        // End of consistent methods -- the following methods are different between
        // `TaskBuilder` and `ContextInsensitiveTaskBuilder`!

        member inline __.ReturnFrom(task : _ Task) =
            bindTask task ret
        member inline __.ReturnFrom(task : Task) =
            bindVoidTask task ret
        member inline __.Bind(task : _ Task, continuation) =
            bindTask task continuation
        member inline __.Bind(task : Task, continuation) =
            bindVoidTask task continuation

    type ContextInsensitiveTaskBuilder() =
        // These methods are consistent between the two builders.
        // Unfortunately, inline members do not work with inheritance.
        member inline __.Delay(f : unit -> Step<_, _>) = f
        member inline __.Run(f : unit -> Step<'m, 'm>) = run f
        member inline __.Zero() = zero()
        member inline __.Return(x) = ret x
        member inline __.ReturnFrom(task) = bindConfiguredTask task ret
        member inline __.ReturnFrom(task) = bindVoidConfiguredTask task ret
        member inline __.ReturnFrom(yld : YieldAwaitable) = bindGenericAwaitable yld ret
        member inline __.Combine(step, continuation) = combine step continuation
        member inline __.Bind(task, continuation) = bindConfiguredTask task continuation
        member inline __.Bind(task, continuation) = bindVoidConfiguredTask task continuation
        member inline __.Bind(yld : YieldAwaitable, continuation) = bindGenericAwaitable yld continuation
        member inline __.While(condition, body) = whileLoop condition body
        member inline __.For(sequence, body) = forLoop sequence body
        member inline __.TryWith(body, catch) = tryWith body catch
        member inline __.TryFinally(body, fin) = tryFinally body fin
        member inline __.Using(disp, body) = using disp body
        // End of consistent methods -- the following methods are different between
        // `TaskBuilder` and `ContextInsensitiveTaskBuilder`!

        member inline __.ReturnFrom(task : _ Task) =
            bindConfiguredTask (task.ConfigureAwait(continueOnCapturedContext = false)) ret
        member inline __.ReturnFrom(task : Task) =
            bindVoidConfiguredTask (task.ConfigureAwait(continueOnCapturedContext = false)) ret
        member inline __.Bind(task : _ Task, continuation) =
            bindConfiguredTask (task.ConfigureAwait(continueOnCapturedContext = false)) continuation
        member inline __.Bind(task : Task, continuation) =
            bindVoidConfiguredTask (task.ConfigureAwait(continueOnCapturedContext = false)) continuation

// Don't warn about our use of the "obsolete" module we just defined (see notes at start of file).
#nowarn "44"

[<AutoOpen>]
module ContextSensitive =
    /// Builds a `System.Threading.Tasks.Task<'a>` similarly to a C# async/await method.
    let task = TaskBuilder.TaskBuilder()

module ContextInsensitive =
    /// Builds a `System.Threading.Tasks.Task<'a>` similarly to a C# async/await method, but with
    /// all awaited tasks automatically configured *not* to resume on the captured context.
    /// This is often preferable when writing library code that is not context-aware, but undesirable when writing
    /// e.g. code that must interact with user interface controls on the same thread as its caller.
    let task = TaskBuilder.ContextInsensitiveTaskBuilder()

