// ----------------------------------------------------------------------------
// F# async extensions (AsyncSeq.fs)
// (c) Tomas Petricek, 2011, Available under Apache 2.0 license.
// ----------------------------------------------------------------------------
namespace FSharpx.Control

open System
open System.Threading
open System.IO
open FSharpx.Control.Utils

// ----------------------------------------------------------------------------

/// An asynchronous sequence represents a delayed computation that can be
/// started to produce either Cons value consisting of the next element of the 
/// sequence (head) together with the next asynchronous sequence (tail) or a 
/// special value representing the end of the sequence (Nil)
type AsyncSeq<'T> = Async<AsyncSeqInner<'T>> 

/// The interanl type that represents a value returned as a result of
/// evaluating a step of an asynchronous sequence
and AsyncSeqInner<'T> =
    | Nil
    | Cons of 'T * AsyncSeq<'T>


/// Module with helper functions for working with asynchronous sequences
module AsyncSeq = 

  /// Creates an empty asynchronou sequence that immediately ends
  [<GeneralizableValue>]
  let empty<'T> : AsyncSeq<'T> = 
    async { return Nil }
 
  /// Creates an asynchronous sequence that generates a single element and then ends
  let singleton (v:'T) : AsyncSeq<'T> = 
    async { return Cons(v, empty) }
    
  /// Generates an async sequence using the specified generator function.
  let rec unfoldAsync (f:'State -> Async<('T * 'State) option>) (s:'State) : AsyncSeq<'T> = 
    f s
    |> Async.map (function
      | Some (a,s) -> Cons(a, unfoldAsync f s)
      | None -> Nil)

  /// Creates an async sequence which repeats the specified value indefinitely.
  let rec replicate (v:'T) : AsyncSeq<'T> =    
    Cons(v, async.Delay <| fun() -> replicate v) |> async.Return    

  /// Yields all elements of the first asynchronous sequence and then 
  /// all elements of the second asynchronous sequence.
  let rec append (seq1: AsyncSeq<'T>) (seq2: AsyncSeq<'T>) : AsyncSeq<'T> = 
    async { let! v1 = seq1
            match v1 with 
            | Nil -> return! seq2
            | Cons (h,t) -> return Cons(h,append t seq2) }


  /// Computation builder that allows creating of asynchronous 
  /// sequences using the 'asyncSeq { ... }' syntax
  type AsyncSeqBuilder() =
    member x.Yield(v) = singleton v
    // This looks weird, but it is needed to allow:
    //
    //   while foo do
    //     do! something
    //
    // because F# translates body as Bind(something, fun () -> Return())
    member x.Return(()) = empty
    member x.YieldFrom(s) = s
    member x.Zero () = empty
    member x.Bind (inp:Async<'T>, body : 'T -> AsyncSeq<'U>) : AsyncSeq<'U> = 
      async.Bind(inp, body)
    member x.Combine (seq1:AsyncSeq<'T>,seq2:AsyncSeq<'T>) = 
      append seq1 seq2
    member x.While (gd, seq:AsyncSeq<'T>) = 
      if gd() then x.Combine(seq,x.Delay(fun () -> x.While (gd, seq))) else x.Zero()
    member x.Delay (f:unit -> AsyncSeq<'T>) = 
      async.Delay(f)

      
  /// Builds an asynchronou sequence using the computation builder syntax
  let asyncSeq = new AsyncSeqBuilder()

  /// Tries to get the next element of an asynchronous sequence
  /// and returns either the value or an exception
  let internal tryNext (input:AsyncSeq<_>) = async { 
    try 
      let! v = input
      return Choice1Of2 v
    with e -> 
      return Choice2Of2 e }

  /// Implements the 'TryWith' functionality for computation builder
  let rec internal tryWith (input : AsyncSeq<'T>) handler =  asyncSeq { 
    let! v = tryNext input
    match v with 
    | Choice1Of2 Nil -> ()
    | Choice1Of2 (Cons (h, t)) -> 
        yield h
        yield! tryWith t handler
    | Choice2Of2 rest -> 
        yield! handler rest }
 
  /// Implements the 'TryFinally' functionality for computation builder
  let rec internal tryFinally (input : AsyncSeq<'T>) compensation = asyncSeq {
      let! v = tryNext input
      match v with 
      | Choice1Of2 Nil -> 
          compensation()
      | Choice1Of2 (Cons (h, t)) -> 
          yield h
          yield! tryFinally t compensation
      | Choice2Of2 e -> 
          compensation()
          yield! raise e }

  /// Creates an asynchronou sequence that iterates over the given input sequence.
  /// For every input element, it calls the the specified function and iterates
  /// over all elements generated by that asynchronous sequence.
  /// This is the 'bind' operation of the computation expression (exposed using
  /// the 'for' keyword in asyncSeq computation).
  let rec collect f (input : AsyncSeq<'T>) : AsyncSeq<'TResult> = asyncSeq {
      let! v = input
      match v with
      | Nil -> ()
      | Cons(h, t) ->
          yield! f h
          yield! collect f t }


  // Add additional methods to the 'asyncSeq' computation builder
  type AsyncSeqBuilder with

    member x.TryFinally (body: AsyncSeq<'T>, compensation) = 
      tryFinally body compensation   

    member x.TryWith (body: AsyncSeq<_>, handler: (exn -> AsyncSeq<_>)) = 
      tryWith body handler

    member x.Using (resource:#IDisposable, binder) = 
      tryFinally (binder resource) (fun () -> 
        if box resource <> null then resource.Dispose())

    /// For loop that iterates over a synchronous sequence (and generates
    /// all elements generated by the asynchronous body)
    member x.For(seq:seq<'T>, action:'T -> AsyncSeq<'TResult>) = 
      let enum = seq.GetEnumerator()
      x.TryFinally(x.While((fun () -> enum.MoveNext()), x.Delay(fun () -> 
        action enum.Current)), (fun () -> 
          if enum <> null then enum.Dispose() ))

    /// Asynchronous for loop - for all elements from the input sequence,
    /// generate all elements produced by the body (asynchronously). See
    /// also the AsyncSeq.collect function.
    member x.For (seq:AsyncSeq<'T>, action:'T -> AsyncSeq<'TResult>) = 
      collect action seq


  // Add asynchronous for loop to the 'async' computation builder
  type Microsoft.FSharp.Control.AsyncBuilder with
    member x.For (seq:AsyncSeq<'T>, action:'T -> Async<unit>) = 
      async.Bind(seq, function
        | Nil -> async.Zero()
        | Cons(h, t) -> async.Combine(action h, x.For(t, action)))

  // --------------------------------------------------------------------------
  // Additional combinators (implemented as async/asyncSeq computations)

  /// Builds a new asynchronous sequence whose elements are generated by 
  /// applying the specified function to all elements of the input sequence.
  ///
  /// The specified function is asynchronous (and the input sequence will
  /// be asked for the next element after the processing of an element completes).
  let mapAsync f (input : AsyncSeq<'T>) : AsyncSeq<'TResult> = asyncSeq {
    for itm in input do 
      let! v = f itm
      yield v }

  /// Asynchronously iterates over the input sequence and generates 'x' for 
  /// every input element for which the specified asynchronous function 
  /// returned 'Some(x)' 
  ///
  /// The specified function is asynchronous (and the input sequence will
  /// be asked for the next element after the processing of an element completes).
  let chooseAsync f (input : AsyncSeq<'T>) : AsyncSeq<'R> = asyncSeq {
    for itm in input do
      let! v = f itm
      match v with 
      | Some v -> yield v 
      | _ -> () }

  /// Builds a new asynchronous sequence whose elements are those from the
  /// input sequence for which the specified function returned true.
  ///
  /// The specified function is asynchronous (and the input sequence will
  /// be asked for the next element after the processing of an element completes).
  let filterAsync f (input : AsyncSeq<'T>) = asyncSeq {
    for v in input do
      let! b = f v
      if b then yield v }

  /// Asynchronously returns the last element that was generated by the
  /// given asynchronous sequence (or the specified default value).
  let rec lastOrDefault def (input : AsyncSeq<'T>) = async {
    let! v = input
    match v with 
    | Nil -> return def
    | Cons(h, t) -> return! lastOrDefault h t }

  /// Asynchronously returns the first element that was generated by the
  /// given asynchronous sequence (or the specified default value).
  let firstOrDefault def (input : AsyncSeq<'T>) = async {
    let! v = input
    match v with 
    | Nil -> return def
    | Cons(h, _) -> return h }

  /// Aggregates the elements of the input asynchronous sequence using the
  /// specified 'aggregation' function. The result is an asynchronous 
  /// sequence of intermediate aggregation result.
  ///
  /// The aggregation function is asynchronous (and the input sequence will
  /// be asked for the next element after the processing of an element completes).
  let rec scanAsync f (state:'TState) (input : AsyncSeq<'T>) = asyncSeq {
    let! v = input
    match v with
    | Nil -> ()
    | Cons(h, t) ->
        let! v = f state h
        yield v
        yield! t |> scanAsync f v }

  /// Iterates over the input sequence and calls the specified function for
  /// every value (to perform some side-effect asynchronously).
  ///
  /// The specified function is asynchronous (and the input sequence will
  /// be asked for the next element after the processing of an element completes).
  let rec iterAsync f (input : AsyncSeq<'T>) = async {
    for itm in input do 
      do! f itm }

  /// Returns an asynchronous sequence that returns pairs containing an element
  /// from the input sequence and its predecessor. Empty sequence is returned for
  /// singleton input sequence.
  let rec pairwise (input : AsyncSeq<'T>) = asyncSeq {
    let! v = input
    match v with
    | Nil -> ()
    | Cons(h, t) ->
        let prev = ref h
        for v in t do
          yield (!prev, v)
          prev := v }

  /// Aggregates the elements of the input asynchronous sequence using the
  /// specified 'aggregation' function. The result is an asynchronous 
  /// workflow that returns the final result.
  ///
  /// The aggregation function is asynchronous (and the input sequence will
  /// be asked for the next element after the processing of an element completes).
  let rec foldAsync f (state:'TState) (input : AsyncSeq<'T>) = 
    input |> scanAsync f state |> lastOrDefault state

  /// Same as AsyncSeq.foldAsync, but the specified function is synchronous
  /// and returns the result of aggregation immediately.
  let rec fold f (state:'TState) (input : AsyncSeq<'T>) = 
    foldAsync (fun st v -> f st v |> async.Return) state input 

  /// Same as AsyncSeq.scanAsync, but the specified function is synchronous
  /// and returns the result of aggregation immediately.
  let rec scan f (state:'TState) (input : AsyncSeq<'T>) = 
    scanAsync (fun st v -> f st v |> async.Return) state input 

  /// Same as AsyncSeq.mapAsync, but the specified function is synchronous
  /// and returns the result of projection immediately.
  let map f (input : AsyncSeq<'T>) = 
    mapAsync (f >> async.Return) input

  /// Same as AsyncSeq.iterAsync, but the specified function is synchronous
  /// and performs the side-effect immediately.
  let iter f (input : AsyncSeq<'T>) = 
    iterAsync (f >> async.Return) input

  /// Same as AsyncSeq.chooseAsync, but the specified function is synchronous
  /// and processes the input element immediately.
  let choose f (input : AsyncSeq<'T>) = 
    chooseAsync (f >> async.Return) input

  /// Same as AsyncSeq.filterAsync, but the specified predicate is synchronous
  /// and processes the input element immediately.
  let filter f (input : AsyncSeq<'T>) =
    filterAsync (f >> async.Return) input
    
  // --------------------------------------------------------------------------
  // Converting from/to synchronous sequences or IObservables

  /// Creates an asynchronous sequence that lazily takes element from an
  /// input synchronous sequence and returns them one-by-one.
  let ofSeq (input : seq<'T>) = asyncSeq {
    for el in input do 
      yield el }

  /// A helper type for implementation of buffering when converting 
  /// observable to an asynchronous sequence
  type internal BufferMessage<'T> = 
    | Get of AsyncReplyChannel<'T>
    | Put of 'T

  /// Converts observable to an asynchronous sequence using an agent with
  /// a body specified as the argument. The returnd async sequence repeatedly 
  /// sends 'Get' message to the agent to get the next element. The observable
  /// sends 'Put' message to the agent (as new inputs are generated).
  let internal ofObservableUsingAgent (input : System.IObservable<_>) f = 
    asyncSeq {  
      use agent = AutoCancelAgent.Start(f)
      use d = input |> Observable.asUpdates
                    |> Observable.subscribe (Put >> agent.Post)
      
      let rec loop() = asyncSeq {
        let! msg = agent.PostAndAsyncReply(Get)
        match msg with
        | ObservableUpdate.Error e -> raise e
        | ObservableUpdate.Completed -> ()
        | ObservableUpdate.Next v ->
            yield v
            yield! loop() }
      yield! loop() }

  /// Converts observable to an asynchronous sequence. Values that are produced
  /// by the observable while the asynchronous sequence is blocked are stored to 
  /// an unbounded buffer and are returned as next elements of the async sequence.
  let ofObservableBuffered (input : System.IObservable<_>) = 
    ofObservableUsingAgent input (fun mbox -> async {
        let buffer = new System.Collections.Generic.Queue<_>()
        let repls = new System.Collections.Generic.Queue<_>()
        while true do
          // Receive next message (when observable ends, caller will
          // cancel the agent, so we need timeout to allow cancleation)
          let! msg = mbox.TryReceive(200)
          match msg with 
          | Some(Put(v)) -> buffer.Enqueue(v)
          | Some(Get(repl)) -> repls.Enqueue(repl)
          | _ -> () 
          // Process matching calls from buffers
          while buffer.Count > 0 && repls.Count > 0 do
            repls.Dequeue().Reply(buffer.Dequeue())  })


  /// Converts observable to an asynchronous sequence. Values that are produced
  /// by the observable while the asynchronous sequence is blocked are discarded
  /// (this function doesn't guarantee that asynchronou ssequence will return 
  /// all values produced by the observable)
  let ofObservable (input : System.IObservable<_>) = 
    ofObservableUsingAgent input (fun mbox -> async {
      while true do 
        // Allow timeout (when the observable ends, caller will
        // cancel the agent, so we need timeout to allow cancellation)
        let! msg = mbox.TryReceive(200)
        match msg with 
        | Some(Put _) | None -> 
            () // Ignore put or no message 
        | Some(Get repl) ->
            // Reader is blocked, so next will be Put
            // (caller will not stop the agent at this point,
            // so timeout is not necessary)
            let! v = mbox.Receive()
            match v with 
            | Put v -> repl.Reply(v)
            | _ -> failwith "Unexpected Get" })

  /// Converts asynchronous sequence to an IObservable<_>. When the client subscribes
  /// to the observable, a new copy of asynchronous sequence is started and is 
  /// sequentially iterated over (at the maximal possible speed). Disposing of the 
  /// observer cancels the iteration over asynchronous sequence. 
  let toObservable (aseq:AsyncSeq<_>) =
    let start (obs:IObserver<_>) =
      async {
        try 
          for v in aseq do obs.OnNext(v)
          obs.OnCompleted()
        with e ->
          obs.OnError(e) }
      |> Async.StartDisposable
    { new IObservable<_> with
        member x.Subscribe(obs) = start obs }

  /// Converts asynchronous sequence to a synchronous blocking sequence.
  /// The elements of the asynchronous sequence are consumed lazily.
  let toBlockingSeq (input : AsyncSeq<'T>) = 
    // Write all elements to a blocking buffer and then add None to denote end
    let buf = new BlockingQueueAgent<_>(1)
    async {
      do! iterAsync (Some >> buf.AsyncAdd) input
      do! buf.AsyncAdd(None) } |> Async.Start

    // Read elements from the blocking buffer & return a sequences
    let rec loop () = seq {
      match buf.Get() with
      | None -> ()
      | Some v -> 
          yield v
          yield! loop() }
    loop ()

  /// Create a new asynchronous sequence that caches all elements of the 
  /// sequence specified as the input. When accessing the resulting sequence
  /// multiple times, the input will still be evaluated only once
  let rec cache (input : AsyncSeq<'T>) = 
    let agent = Agent<AsyncReplyChannel<_>>.Start(fun agent -> async {
      let! (repl:AsyncReplyChannel<AsyncSeqInner<'T>>) = agent.Receive()
      let! next = input
      let res = 
        match next with 
        | Nil -> Nil
        | Cons(h, t) -> Cons(h, cache t)
      repl.Reply(res)
      while true do
        let! (repl:AsyncReplyChannel<AsyncSeqInner<'T>>) = agent.Receive()
        repl.Reply(res) })
    async { return! agent.PostAndAsyncReply(id) }

  // --------------------------------------------------------------------------

  /// Threads a state through the mapping over an async sequence using an async function.
  let rec threadStateAsync (f:'s -> 'a -> Async<'b * 's>) (st:'s) (s:AsyncSeq<'a>) : AsyncSeq<'b> = asyncSeq {
    let! s = s
    match s with
    | Nil -> ()
    | Cons(a,tl) ->       
      let! b,st' = f st a
      yield b
      yield! threadStateAsync f st' tl }

  /// Combines two asynchronous sequences into a sequence of pairs. 
  /// The values from sequences are retrieved in parallel. 
  /// The resulting sequence stops when either of the argument sequences stop.
  let rec zip (input1 : AsyncSeq<'T1>) (input2 : AsyncSeq<'T2>) : AsyncSeq<_> = async {
    let! ft = input1 |> Async.StartChild
    let! s = input2
    let! f = ft
    match f, s with 
    | Cons(hf, tf), Cons(hs, ts) ->
        return Cons( (hf, hs), zip tf ts)
    | _ -> return Nil }

  /// Combines two asynchronous sequences using the specified function.
  /// The values from sequences are retrieved in parallel.
  /// The resulting sequence stops when either of the argument sequences stop.
  let rec zipWithAsync (z:'a -> 'b -> Async<'c>) (a:AsyncSeq<'a>) (b:AsyncSeq<'b>) : AsyncSeq<'c> = async {
    let! a,b = Async.Parallel(a, b)    
    match a,b with 
    | Cons(a, atl), Cons(b, btl) ->
      let! c = z a b
      return Cons(c, zipWithAsync z atl btl)
    | _ -> return Nil }

  /// Combines two asynchronous sequences using the specified function.
  /// The values from sequences are retrieved in parallel.
  /// The resulting sequence stops when either of the argument sequences stop.
  let inline zipWith (z:'a -> 'b -> 'c) (a:AsyncSeq<'a>) (b:AsyncSeq<'b>) : AsyncSeq<'c> =
    zipWithAsync (fun a b -> z a b |> async.Return) a b

  /// Combines two asynchronous sequences using the specified function to which it also passes the index.
  /// The values from sequences are retrieved in parallel.
  /// The resulting sequence stops when either of the argument sequences stop.
  let inline zipWithIndexAsync (f:int -> 'a -> Async<'b>) (s:AsyncSeq<'a>) : AsyncSeq<'b> =
    threadStateAsync (fun i a -> f i a |> Async.map (fun b -> b,i + 1)) 0 s        

  /// Feeds an async sequence of values into an async sequence of async functions.
  let inline zappAsync (fs:AsyncSeq<'a -> Async<'b>>) (s:AsyncSeq<'a>) : AsyncSeq<'b> =
    zipWithAsync ((|>)) s fs

  /// Feeds an async sequence of values into an async sequence of functions.
  let inline zapp (fs:AsyncSeq<'a -> 'b>) (s:AsyncSeq<'a>) : AsyncSeq<'b> =
    zipWith ((|>)) s fs

  /// Traverses an async sequence an applies to specified function such that if None is returned the traversal short-circuits
  /// and None is returned as the result. Otherwise, the entire sequence is traversed and the result returned as Some.
  let rec traverseOptionAsync (f:'a -> Async<'b option>) (s:AsyncSeq<'a>) : Async<AsyncSeq<'b> option> = async {
    let! s = s
    match s with
    | Nil -> return Some (Nil |> async.Return)
    | Cons(a,tl) ->
      let! b = f a
      match b with
      | Some b -> 
        return! traverseOptionAsync f tl |> Async.map (Option.map (fun tl -> Cons(b, tl) |> async.Return))
      | None -> 
        return None }

  /// Traverses an async sequence an applies to specified function such that if Choice2Of2 is returned the traversal short-circuits
  /// and Choice2Of2 is returned as the result. Otherwise, the entire sequence is traversed and the result returned as Choice1Of2.
  let rec traverseChoiceAsync (f:'a -> Async<Choice<'b, 'e>>) (s:AsyncSeq<'a>) : Async<Choice<AsyncSeq<'b>, 'e>> = async {
    let! s = s
    match s with
    | Nil -> return Choice1Of2 (Nil |> async.Return)
    | Cons(a,tl) ->
      let! b = f a
      match b with
      | Choice1Of2 b -> 
        return! traverseChoiceAsync f tl |> Async.map (Choice.mapl (fun tl -> Cons(b, tl) |> async.Return))
      | Choice2Of2 e -> 
        return Choice2Of2 e }

  /// Returns an async computation which iterates the async sequence for
  /// its side-effects.
  let inline runAsync (s:AsyncSeq<unit>) : Async<unit> =
    s |> iter id

  /// Iterates an async sequence using the specified function until it returns false.
  let inline iterWhileAsync (f:'a -> Async<bool>) (s:AsyncSeq<'a>) : Async<unit> =    
    let rec sink() = asyncSeq {
      let c = new AsyncResultCell<_>()
      yield (fun a -> f a |> Async.map (fun f -> c.RegisterResult(AsyncOk f, true)))
      let! flag = c.AsyncResult
      if flag then yield! sink()
      else () }
    zappAsync (sink()) s |> runAsync

  /// Returns elements from an asynchronous sequence while the specified 
  /// predicate holds. The predicate is evaluated asynchronously.
  let rec takeWhileAsync p (input : AsyncSeq<'T>) : AsyncSeq<_> = async {
    let! v = input
    match v with
    | Cons(h, t) -> 
        let! res = p h
        if res then 
          return Cons(h, takeWhileAsync p t)
        else return Nil
    | Nil -> return Nil }

  /// Skips elements from an asynchronous sequence while the specified 
  /// predicate holds and then returns the rest of the sequence. The 
  /// predicate is evaluated asynchronously.
  let rec skipWhileAsync p (input : AsyncSeq<'T>) : AsyncSeq<_> = async {
    let! v = input
    match v with
    | Cons(h, t) ->
        let! res = p h
        if res then return! skipWhileAsync p t
        else return v
    | Nil -> return Nil }

  /// Returns elements from an asynchronous sequence while the specified 
  /// predicate holds. The predicate is evaluated synchronously.
  let rec takeWhile p (input : AsyncSeq<'T>) = 
    takeWhileAsync (p >> async.Return) input

  /// Skips elements from an asynchronous sequence while the specified 
  /// predicate holds and then returns the rest of the sequence. The 
  /// predicate is evaluated asynchronously.
  let rec skipWhile p (input : AsyncSeq<'T>) = 
    skipWhileAsync (p >> async.Return) input

  /// Returns the first N elements of an asynchronous sequence
  let rec take count (input : AsyncSeq<'T>) : AsyncSeq<_> = async {
    if count > 0 then
      let! v = input
      match v with
      | Cons(h, t) -> 
          return Cons(h, take (count - 1) t)
      | Nil -> return Nil 
    else return Nil }

  /// Skips the first N elements of an asynchronous sequence and
  /// then returns the rest of the sequence unmodified.
  let rec skip count (input : AsyncSeq<'T>) : AsyncSeq<_> = async {
    if count > 0 then
      let! v = input
      match v with
      | Cons(h, t) -> 
          return! skip (count - 1) t
      | Nil -> return Nil 
    else return! input }

  /// Creates an async computation which iterates the AsyncSeq and collects the output into an array.
  let toArray (input:AsyncSeq<'T>) : Async<'T[]> =
    input
    |> fold (fun (arr:ResizeArray<_>) a -> arr.Add(a) ; arr) (new ResizeArray<_>()) 
    |> Async.map (fun arr -> arr.ToArray())

  /// Creates an async computation which iterates the AsyncSeq and collects the output into a list.
  let toList (input:AsyncSeq<'T>) : Async<'T list> =
    input 
    |> fold (fun arr a -> a::arr) []
    |> Async.map List.rev

  /// Flattens an AsyncSeq of sequences.
  let rec concatSeq (input:AsyncSeq<#seq<'T>>) : AsyncSeq<'T> = asyncSeq {
    let! v = input
    match v with
    | Nil -> ()
    | Cons (hd, tl) ->
      for item in hd do 
        yield item
      yield! concatSeq tl }

  /// Interleaves two async sequences into a resulting sequence. The provided
  /// sequences are consumed in lock-step.
  let interleave = 

    let rec left (a:AsyncSeq<'a>) (b:AsyncSeq<'b>) : AsyncSeq<Choice<_,_>> = async {
      let! a = a        
      match a with
      | Cons (a1, t1) -> return Cons (Choice1Of2 a1, right t1 b)
      | Nil -> return! b |> map Choice2Of2 }

    and right (a:AsyncSeq<'a>) (b:AsyncSeq<'b>) : AsyncSeq<Choice<_,_>> = async {
      let! b = b        
      match b with
      | Cons (a2, t2) -> return Cons (Choice2Of2 a2, left a t2)
      | Nil -> return! a |> map Choice1Of2 }

    left

  /// Buffer items from the async sequence into buffers of a specified size.
  /// The last buffer returned may be less than the specified buffer size.
  let rec bufferByCount (bufferSize:int) (s:AsyncSeq<'T>) : AsyncSeq<'T[]> = 
    if (bufferSize < 1) then invalidArg "bufferSize" "must be positive"
    async {                  
      let buffer = ResizeArray<_>()
      let rec loop s = async {
        let! step = s
        match step with
        | Nil ->
          if (buffer.Count > 0) then return Cons(buffer.ToArray(),async.Return Nil)
          else return Nil
        | Cons(a,tl) ->
          buffer.Add(a)
          if buffer.Count = bufferSize then 
            let buf = buffer.ToArray()
            buffer.Clear()
            return Cons(buf, loop tl)
          else 
            return! loop tl            
      }
      return! loop s
    }

  /// Merges two async sequences into an async sequence non-deterministically.
  let rec merge (a:AsyncSeq<'a>) (b:AsyncSeq<'a>) : AsyncSeq<'a> = async {
    let! one,other = Async.chooseBoth a b
    match one with
    | Nil -> return! other
    | Cons(hd,tl) ->
      return Cons(hd, merge tl other) }

  /// Merges all specified async sequences into an async sequence non-deterministically.
  let rec mergeAll (ss:AsyncSeq<'a> list) : AsyncSeq<'a> =
    match ss with
    | [] -> empty
    | [s] -> s
    | [a;b] -> merge a b
    | hd::tl -> merge hd (mergeAll tl)
    


[<AutoOpen>]
module AsyncSeqExtensions = 
  /// Builds an asynchronou sequence using the computation builder syntax
  let asyncSeq = new AsyncSeq.AsyncSeqBuilder()

  // Add asynchronous for loop to the 'async' computation builder
  type Microsoft.FSharp.Control.AsyncBuilder with
    member x.For (seq:AsyncSeq<'T>, action:'T -> Async<unit>) = 
      async.Bind(seq, function
        | Nil -> async.Zero()
        | Cons(h, t) -> async.Combine(action h, x.For(t, action)))

module Seq = 
  open FSharpx.Control

  /// Converts asynchronous sequence to a synchronous blocking sequence.
  /// The elements of the asynchronous sequence are consumed lazily.
  let ofAsyncSeq (input : AsyncSeq<'T>) =
    AsyncSeq.toBlockingSeq input