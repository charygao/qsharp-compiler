﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

module Microsoft.Quantum.QsCompiler.Targeting.CallGraphWalker

open System
open System.Collections.Generic
open System.Collections.Immutable
open Microsoft.Quantum.QsCompiler
open Microsoft.Quantum.QsCompiler.DataTypes
open Microsoft.Quantum.QsCompiler.SyntaxTree
open Microsoft.Quantum.QsCompiler.Transformations.Core


type private SpecializationKey =
    {
        CallableName : QsQualifiedName
        Kind : QsSpecializationKind
        TypeArgHash : QsNullable<int list>
    }


/// Class used to track call graph of a compilation.
/// This class is *not* threadsafe.
type CallGraph() =

    let dependencies = new Dictionary<SpecializationKey, HashSet<SpecializationKey>>()
    let typeHashes = new Dictionary<int, ResolvedType>()

    let SpecInfoToKey kind parent typeArgs =
        let getTypeArgHash (tArgs : ImmutableArray<ResolvedType>) =
            let pushHash t =
                let tHash = Hashing.TypeHash t
                typeHashes.[tHash] <- t
                tHash
            tArgs |> Seq.map pushHash |> Seq.toList
        let typeArgHash = typeArgs |> QsNullable<_>.Map getTypeArgHash
        { Kind = kind; CallableName = parent; TypeArgHash = typeArgHash }

    let SpecToKey (spec : QsSpecialization) =
        SpecInfoToKey spec.Kind spec.Parent spec.TypeArguments

    let HashToTypeArgs (tArgHash : QsNullable<int list>) =
        let getResolvedType = typeHashes.TryGetValue >> function
            | true, t -> t
            | false, _ -> new ArgumentException "no type with the given hash has been listed" |> raise
        tArgHash |> QsNullable<_>.Map (fun hashes -> hashes |> List.map getResolvedType)

    let RecordDependency callerKey calledKey =
        match dependencies.TryGetValue(callerKey) with
        | true, deps -> deps.Add(calledKey) |> ignore
        | false, _ -> let newDeps = new HashSet<SpecializationKey>()
                      newDeps.Add(calledKey) |> ignore
                      dependencies.[callerKey] <- newDeps

    member this.AddDependency(callerSpec, calledKind, calledName, calledTypeArgs) =
        let callerKey = SpecToKey callerSpec
        let calledKey = SpecInfoToKey calledKind calledName calledTypeArgs
        RecordDependency callerKey calledKey

    member this.AddDependency(callerKind, callerName, callerTypeArgs, calledKind, calledName, calledTypeArgs) =
        let callerKey = SpecInfoToKey callerKind callerName callerTypeArgs
        let calledKey = SpecInfoToKey calledKind calledName calledTypeArgs
        RecordDependency callerKey calledKey

    /// Returns all specializations that are used directly within the given caller,
    /// whether they are called, partially applied, or assigned.
    /// The returned specializations are identified by the full name of the callable,
    /// the specialization kind, as well as the resolved type arguments.
    /// The returned type arguments are the exact type arguments of the expression,
    /// and may thus be incomplete or correspond to subtypes of a defined specialization bundle.
    member this.GetDirectDependencies callerSpec =
        let key = SpecToKey callerSpec
        match dependencies.TryGetValue key with
        | true, deps -> deps
                        |> Seq.map (fun key -> (key.CallableName, key.Kind, key.TypeArgHash |> HashToTypeArgs))
                        |> ImmutableArray.CreateRange
        | false, _ -> ImmutableArray.Empty

    /// Returns all specializations directly or indirectly used within the given caller,
    /// whether they are called, partially applied, or assigned.
    /// The returned specializations are identified by the full name of the callable,
    /// the specialization kind, as well as the resolved type arguments.
    /// The returned type arguments are the exact type arguments of the expression,
    /// and may thus be incomplete or correspond to subtypes of a defined specialization bundle.
    member this.GetAllDependencies callerSpec =
        let rec WalkDependencyTree root (accum : HashSet<SpecializationKey>) =
            match dependencies.TryGetValue(root) with
            | true, next ->
                next
                |> Seq.fold (fun (a : HashSet<SpecializationKey>) k ->
                    if a.Add(k) then WalkDependencyTree k a else a)
                    accum
            | false, _ -> accum
        let key = SpecToKey callerSpec
        WalkDependencyTree key (new HashSet<SpecializationKey>())
        |> Seq.map (fun key -> (key.CallableName, key.Kind, key.TypeArgHash |> HashToTypeArgs))
        |> ImmutableArray.CreateRange


type private ExpressionKindGraphBuilder(exprXformer : ExpressionGraphBuilder, graph : CallGraph,
        spec : QsSpecialization) =
    inherit ExpressionKindWalker()

    let mutable inCall = false
    let mutable adjoint = false
    let mutable controlled = false

    override this.ExpressionWalker x = exprXformer.Walk x
    override this.TypeWalker x = ()

    member private this.HandleCall method arg =
        inCall <- true
        adjoint <- false
        controlled <- false
        this.ExpressionWalker method
        inCall <- false
        this.ExpressionWalker arg

    override this.onOperationCall(method, arg) =
        this.HandleCall method arg

    override this.onFunctionCall(method, arg) =
        this.HandleCall method arg

    override this.onAdjointApplication(ex) =
        adjoint <- not adjoint
        base.onAdjointApplication(ex)

    override this.onControlledApplication(ex) =
        controlled <- true
        base.onControlledApplication(ex)

    override this.onIdentifier(sym, explicitTypeArgs) =
        match sym with
        | GlobalCallable(name) ->
            let typeArgs = explicitTypeArgs // FIXME: THIS IS NOT ACCURATE
            if inCall
            then
                let kind = match adjoint, controlled with
                           | false, false -> QsBody
                           | false, true  -> QsControlled
                           | true,  false -> QsAdjoint
                           | true,  true  -> QsControlledAdjoint
                graph.AddDependency(spec, kind, name, typeArgs)
            else
                // The callable is being used in a non-call context, such as being
                // assigned to a variable or passed as an argument to another callable,
                // which means it could get a functor applied at some later time.
                // We're conservative and add all 4 possible kinds.
                graph.AddDependency(spec, QsBody, name, typeArgs)
                graph.AddDependency(spec, QsControlled, name, typeArgs)
                graph.AddDependency(spec, QsAdjoint, name, typeArgs)
                graph.AddDependency(spec, QsControlledAdjoint, name, typeArgs)
        | _ -> ()


and private ExpressionGraphBuilder(graph : CallGraph, spec : QsSpecialization) as this =
    inherit ExpressionWalker()

    let kindXformer = new ExpressionKindGraphBuilder(this, graph, spec)

    override this.Kind = upcast kindXformer


type private StatementGraphBuilder(scopeXformer : ScopeGraphBuilder, graph : CallGraph,
        spec : QsSpecialization) =
    inherit StatementKindWalker()

    let exprXformer = new ExpressionGraphBuilder(graph, spec)

    override this.ScopeWalker x = scopeXformer.Walk x

    override this.ExpressionWalker x = exprXformer.Walk x
    override this.TypeWalker x = ()
    override this.LocationWalker x = ()


and private ScopeGraphBuilder(graph : CallGraph, spec : QsSpecialization) as this =
    inherit ScopeWalker()

    let kindXformer = new StatementGraphBuilder(this, graph, spec)

    override this.StatementKind = upcast kindXformer


type TreeGraphBuilder() =
    inherit SyntaxTreeWalker()

    let graph = new CallGraph()

    let mutable scopeXform = None : ScopeGraphBuilder option

    override this.Scope with get() = scopeXform |> Option.map (fun x -> x :> ScopeWalker)
                                                |> Option.defaultWith (fun () -> new ScopeWalker())

    override this.onSpecializationImplementation(s) =
        let xform = new ScopeGraphBuilder(graph, s)
        scopeXform <- Some xform
        base.onSpecializationImplementation(s)

    member this.CallGraph with get() = graph