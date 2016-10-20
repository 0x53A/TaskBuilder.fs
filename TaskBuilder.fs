﻿// TaskBuilder.fs - TPL task computation expressions for F#
//
// Written in 2016 by Robert Peele (humbobst@gmail.com)
//
// To the extent possible under law, the author(s) have dedicated all copyright and related and neighboring rights
// to this software to the public domain worldwide. This software is distributed without any warranty.
//
// You should have received a copy of the CC0 Public Domain Dedication along with this software.
// If not, see <http://creativecommons.org/publicdomain/zero/1.0/>.

module TaskBuilder
open System
open System.Threading.Tasks
open System.Runtime.CompilerServices

type Step<'a, 'm> =
    struct
        val public ImmediateValue : 'a
        val public Continuation : StepContinuation<'a, 'm>
        new(immediate, continuation) = { ImmediateValue = immediate; Continuation = continuation }
        static member OfImmediate(immediate) = Step(immediate, Unchecked.defaultof<_>)
        static member OfContinuation(con) = Step(Unchecked.defaultof<_>, con)
    end
and
    [<AllowNullLiteral>]
    StepContinuation<'a, 'm> =
        val public Await : StepStateMachine<'m> -> bool
        val public NextStep : unit -> Step<'a, 'm>
        new(await, nextStep) = { Await = await; NextStep = nextStep }
and StepStateMachine<'m>(step : Step<'m, 'm>) =
    let mutable methodBuilder = AsyncTaskMethodBuilder<'m>()
    let mutable step = step
    let mutable nextStep = Unchecked.defaultof<_>
    let mutable awaiting = false
    let mutable faulted = false
    member this.Run() =
        let mutable this = this
        methodBuilder.Start(&this)
        methodBuilder.Task

    member this.Await(awaitable : 'await when 'await :> INotifyCompletion) =
        awaiting <- true
        let mutable this = this
        let mutable awaiter = awaitable
        methodBuilder.AwaitOnCompleted(&awaiter, &this)
        false
    member this.MoveNext() =
        if awaiting then
            awaiting <- false
            step <-
                try nextStep() with
                | exn -> 
                    methodBuilder.SetException(exn)
                    faulted <- true
                    Step<'m, 'm>.OfImmediate(Unchecked.defaultof<_>)
        if faulted then
            ()
        elif isNull (box step.Continuation) then
            methodBuilder.SetResult(step.ImmediateValue)
        else
            let moveNext =
                try
                    let stepAwaiter = step.Continuation
                    awaiting <- true
                    nextStep <- stepAwaiter.NextStep
                    stepAwaiter.Await(this)
                with
                | exn ->
                    methodBuilder.SetException(exn)
                    faulted <- true
                    false
            if moveNext then
                this.MoveNext()
    interface IAsyncStateMachine with
        member this.MoveNext() = this.MoveNext()
        member this.SetStateMachine(_) = ()

module Step =
    let zero() = Step<unit, _>.OfImmediate(())

    let ret (x : 'a) = Step<'a, 'a>.OfImmediate(x)

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

    let inline bindGenericAwaitable< ^a, ^b, ^c, ^m when ^a : (member GetAwaiter : unit -> ^b) and ^b :> INotifyCompletion >
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

    let rec tryWithCore (step : Step<'a, 'm>) (catch : exn -> Step<'a, 'm>) =
        let stepContinuation = step.Continuation
        if isNull stepContinuation then
            step
        else
            let stepNext = stepContinuation.NextStep
            StepContinuation
                ( stepContinuation.Await
                , fun () ->
                    try
                        tryWithCore (stepNext()) catch
                    with
                    | exn -> catch exn
                ) |> Step<'a, 'm>.OfContinuation

    let inline tryWith step catch =
        try
            tryWithCore (step()) catch
        with
        | exn -> catch exn

    let rec tryFinallyCore (step : Step<'a, 'm>) (fin : unit -> unit) =
        let stepContinuation = step.Continuation
        if isNull stepContinuation then
            fin()
            step
        else
            let stepNext = stepContinuation.NextStep
            StepContinuation
                ( stepContinuation.Await
                , fun () ->
                    try
                        tryFinallyCore (stepNext()) fin
                    with
                    | _ ->
                        fin()
                        reraise()
                ) |> Step<'a, 'm>.OfContinuation

    let inline tryFinally step fin =
        try
            tryFinallyCore (step()) fin
        with
        | _ ->
            fin()
            reraise()

    let inline using (disp : #IDisposable) (body : _ -> Step<'a, 'm>) =
        tryFinally (fun () -> body disp) disp.Dispose

    let forLoop (sequence : 'a seq) (body : 'a -> Step<unit, 'm>) =
        using (sequence.GetEnumerator())
            (fun e -> whileLoop e.MoveNext (fun () -> body e.Current))

    let run (step : Step<'m, 'm>) =
        if isNull (box step.Continuation) then
            Task.FromResult(step.ImmediateValue)
        else
            let state = StepStateMachine<'m>(step)
            state.Run()

open Step

type Await<'x>(value : 'x) =
    struct
        member this.Value = value
    end

/// Await a generic awaitable (with no result value).
let await x = Await(x)

type TaskBuilder() =
    member inline __.Delay(f : unit -> Step<_, _>) = f
    member inline __.Run(f : unit -> Step<'m, 'm>) = run (f())

    member inline __.Zero() = zero()
    member inline __.Return(x) = ret x
    member inline __.ReturnFrom(task) = bindTask task ret
    member inline __.ReturnFrom(task) = bindVoidTask task ret
    member inline __.ReturnFrom(yld : YieldAwaitable) = bindGenericAwaitable yld ret
    member inline __.ReturnFrom(awt : _ Await) = bindGenericAwaitable awt.Value ret
    member inline __.Combine(step, continuation) = combine step continuation
    member inline __.Bind(task, continuation) = bindTask task continuation
    member inline __.Bind(task, continuation) = bindVoidTask task continuation
    member inline __.Bind(yld : YieldAwaitable, continuation) = bindGenericAwaitable yld continuation
    member inline __.ReturnFrom(awt : _ Await, continuation) = bindGenericAwaitable awt.Value continuation
    member inline __.While(condition, body) = whileLoop condition body
    member inline __.For(sequence, body) = forLoop sequence body
    member inline __.TryWith(body, catch) = tryWith body catch
    member inline __.TryFinally(body, fin) = tryFinally body fin
    member inline __.Using(disp, body) = using disp body

let task = TaskBuilder()