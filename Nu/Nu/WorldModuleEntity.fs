﻿// Nu Game Engine.
// Copyright (C) Bryan Edds, 2013-2020.

namespace Nu
open System
open System.Collections.Generic
open System.Numerics
open System.IO
open Prime
open Nu

[<AutoOpen; ModuleBinding>]
module WorldModuleEntity =

    /// Dynamic property getters.
    let internal Getters = Dictionary<string, Entity -> World -> Property> HashIdentity.Structural

    /// Dynamic property setters.
    let internal Setters = Dictionary<string, Property -> Entity -> World -> bool * World> HashIdentity.Structural

    /// Mutable clipboard that allows its state to persist beyond undo / redo.
    let mutable private Clipboard : obj option = None

    // OPTIMIZATION: avoids closure allocation in tight-loop.
    type private KeyEquality () =
        inherit OptimizedClosures.FSharpFunc<
            KeyValuePair<Entity Address, UMap<Entity Address, EntityState>>,
            KeyValuePair<Entity Address, UMap<Entity Address, EntityState>>,
            bool> ()
        override this.Invoke _ = failwithumf ()
        override this.Invoke
            (entityStateKey : KeyValuePair<Entity Address, UMap<Entity Address, EntityState>>,
             entityStateKey2 : KeyValuePair<Entity Address, UMap<Entity Address, EntityState>>) =
            refEq entityStateKey.Key entityStateKey2.Key &&
            refEq entityStateKey.Value entityStateKey2.Value
    let private keyEquality = KeyEquality ()

    // OPTIMIZATION: avoids closure allocation in tight-loop.
    let mutable private getFreshKeyAndValueEntity = Unchecked.defaultof<Entity>
    let mutable private getFreshKeyAndValueWorld = Unchecked.defaultof<World>
    let private getFreshKeyAndValue () =
        let entityStateOpt =
            match UMap.tryGetValue getFreshKeyAndValueEntity.EntityAddress getFreshKeyAndValueWorld.EntityStates with
            | (true, entityStateOpt) -> entityStateOpt
            | (false, _) -> Unchecked.defaultof<EntityState>
        KeyValuePair (KeyValuePair (getFreshKeyAndValueEntity.EntityAddress, getFreshKeyAndValueWorld.EntityStates), entityStateOpt)
    let private getFreshKeyAndValueCached =
        getFreshKeyAndValue

    // OPTIMIZATION: cache one entity change address
    let mutable changeEventNamesFree = true
    let changeEventNamesCached = [|"Change"; ""; "Event"; ""; ""; ""|]

    type World with

        // OPTIMIZATION: a ton of optimization has gone down in here...!
        static member private entityStateFinder (entity : Entity) world =
            let entityStateOpt = entity.EntityStateOpt
            if isNull (entityStateOpt :> obj) || entityStateOpt.Invalidated then
                getFreshKeyAndValueEntity <- entity
                getFreshKeyAndValueWorld <- world
                let entityStateOpt =
                    KeyedCache.getValueFast
                        keyEquality
                        getFreshKeyAndValueCached
                        (KeyValuePair (entity.EntityAddress, world.EntityStates))
                        (World.getEntityCachedOpt world)
                getFreshKeyAndValueEntity <- Unchecked.defaultof<Entity>
                getFreshKeyAndValueWorld <- Unchecked.defaultof<World>
                match entityStateOpt :> obj with
                | null -> Unchecked.defaultof<EntityState>
                | _ ->
                    if entityStateOpt.Imperative then entity.EntityStateOpt <- entityStateOpt
                    entityStateOpt
            else entityStateOpt

        static member private entityStateAdder entityState (entity : Entity) world =
            let screenDirectory =
                match Address.getNames entity.EntityAddress with
                | [|screenName; layerName; entityName|] ->
                    match UMap.tryFind screenName world.ScreenDirectory with
                    | Some layerDirectory ->
                        match UMap.tryFind layerName layerDirectory.Value with
                        | Some entityDirectory ->
                            let entityDirectory' = UMap.add entityName entity.EntityAddress entityDirectory.Value
                            let layerDirectory' = UMap.add layerName (KeyValuePair (entityDirectory.Key, entityDirectory')) layerDirectory.Value
                            UMap.add screenName (KeyValuePair (layerDirectory.Key, layerDirectory')) world.ScreenDirectory
                        | None -> failwith ("Cannot add entity '" + scstring entity.EntityAddress + "' to non-existent layer.")
                    | None -> failwith ("Cannot add entity '" + scstring entity.EntityAddress + "' to non-existent screen.")
                | _ -> failwith ("Invalid entity address '" + scstring entity.EntityAddress + "'.")
            let entityStates = UMap.add entity.EntityAddress entityState world.EntityStates
            World.choose { world with ScreenDirectory = screenDirectory; EntityStates = entityStates }

        static member private entityStateRemover (entity : Entity) world =
            let screenDirectory =
                match Address.getNames entity.EntityAddress with
                | [|screenName; layerName; entityName|] ->
                    match UMap.tryFind screenName world.ScreenDirectory with
                    | Some layerDirectory ->
                        match UMap.tryFind layerName layerDirectory.Value with
                        | Some entityDirectory ->
                            let entityDirectory' = UMap.remove entityName entityDirectory.Value
                            let layerDirectory' = UMap.add layerName (KeyValuePair (entityDirectory.Key, entityDirectory')) layerDirectory.Value
                            UMap.add screenName (KeyValuePair (layerDirectory.Key, layerDirectory')) world.ScreenDirectory
                        | None -> failwith ("Cannot remove entity '" + scstring entity.EntityAddress + "' from non-existent layer.")
                    | None -> failwith ("Cannot remove entity '" + scstring entity.EntityAddress + "' from non-existent screen.")
                | _ -> failwith ("Invalid entity address '" + scstring entity.EntityAddress + "'.")
            let entityStates = UMap.remove entity.EntityAddress world.EntityStates
            World.choose { world with ScreenDirectory = screenDirectory; EntityStates = entityStates }

        static member private entityStateSetter entityState (entity : Entity) world =
#if DEBUG
            if not (UMap.containsKey entity.EntityAddress world.EntityStates) then
                failwith ("Cannot set the state of a non-existent entity '" + scstring entity.EntityAddress + "'")
#endif
            let entityStates = UMap.add entity.EntityAddress entityState world.EntityStates
            World.choose { world with EntityStates = entityStates }

        static member private addEntityState entityState (entity : Entity) world =
            World.entityStateAdder entityState entity world

        static member private removeEntityState (entity : Entity) world =
            World.entityStateRemover entity world

        static member private shouldPublishEntityChange alwaysPublish (entityState : EntityState) =
            alwaysPublish || entityState.PublishChanges

        static member private publishEntityChange propertyName (propertyValue : obj) (entity : Entity) world =
            let changeData = { Name = propertyName; Value = propertyValue }
            let entityNames = Address.getNames entity.EntityAddress
            let mutable changeEventNamesUtilized = false
            let changeEventAddress =
                if changeEventNamesFree then
                    changeEventNamesFree <- false
                    changeEventNamesUtilized <- true
                    changeEventNamesCached.[1] <- propertyName
                    changeEventNamesCached.[3] <- entityNames.[0]
                    changeEventNamesCached.[4] <- entityNames.[1]
                    changeEventNamesCached.[5] <- entityNames.[2]
                    rtoa<ChangeData> changeEventNamesCached
                else rtoa<ChangeData> [|"Change"; propertyName; "Event"; entityNames.[0]; entityNames.[1]; entityNames.[2]|]
            let eventTrace = EventTrace.debug "World" "publishEntityChange" EventTrace.empty
            let sorted = propertyName = "ParentNodeOpt"
            let world = World.publishPlus changeData changeEventAddress eventTrace entity sorted world
            if changeEventNamesUtilized then changeEventNamesFree <- true
            world

        static member private getEntityStateOpt entity world =
            let entityStateOpt = World.entityStateFinder entity world
            if isNull (entityStateOpt :> obj) then None
            else Some entityStateOpt

        static member internal getEntityState entity world =
#if DEBUG
            match World.getEntityStateOpt entity world with
            | Some entityState -> entityState
            | None -> failwith ("Could not find entity with address '" + scstring entity.EntityAddress + "'.")
#else
            World.entityStateFinder entity world
#endif

        static member internal getEntityXtensionProperties entity world =
            let entityState = World.getEntityState entity world
            entityState.Xtension |> Xtension.toSeq |> Seq.toList

        static member private setEntityState entityState entity world =
            World.entityStateSetter entityState entity world

        static member private updateEntityStateInternal updater (entityState : EntityState) entity world =
            match updater entityState with
            | Some (entityState : EntityState) ->
                if entityState.Imperative
                then (true, entityState, world)
                else (true, entityState, World.setEntityState entityState entity world)
            | None -> (false, entityState, world)

        static member private updateEntityStateWithoutEvent updater entity world =
            let entityState = World.getEntityState entity world
            let (changed, _, world) = World.updateEntityStateInternal updater entityState entity world
            (changed, world)

        static member private updateEntityState updater alwaysPublish propertyName propertyValue entity world =
            let entityState = World.getEntityState entity world
            let (changed, entityState, world) = World.updateEntityStateInternal updater entityState entity world
            if changed && World.shouldPublishEntityChange alwaysPublish entityState
            then World.publishEntityChange propertyName propertyValue entity world
            else world

        static member private updateEntityStatePlus updater alwaysPublish propertyName propertyValue entity world =

            // cache old values
            let oldWorld = world
            let oldEntityState = World.getEntityState entity oldWorld
            let oldOmnipresent = oldEntityState.Omnipresent

            // OPTIMIZATION: don't update entity tree if entity is omnipresent
            let (changed, entityState, world) =
                if not oldOmnipresent then
                    let oldOmnipresent = oldEntityState.Omnipresent
                    let oldAbsolute = oldEntityState.Absolute
                    let oldBoundsMax = if not oldEntityState.Omnipresent then World.getEntityStateBoundsMax oldEntityState else v4Zero
                    let (changed, entityState, world) = World.updateEntityStateInternal updater oldEntityState entity world
                    let world =
                        if changed
                        then World.updateEntityInEntityTree oldOmnipresent oldAbsolute oldBoundsMax entity oldWorld world
                        else world
                    (changed, entityState, world)
                else World.updateEntityStateInternal updater oldEntityState entity world

            // publish entity change event if needed
            if changed && World.shouldPublishEntityChange alwaysPublish entityState
            then World.publishEntityChange propertyName propertyValue entity world
            else world

        static member private publishEntityChanges entity world =
            let entityState = World.getEntityState entity world
            let properties = World.getProperties entityState
            if entityState.PublishChanges then
                List.fold (fun world (propertyName, _, propertyValue) ->
                    World.publishEntityChange propertyName propertyValue entity world)
                    world properties
            else world

        static member internal getEntityExists entity world =
            Option.isSome (World.getEntityStateOpt entity world)

        static member internal getEntityStateBoundsMax entityState =
            // TODO: get up off yer arse and write an algorithm for tight-fitting bounds...
            let transform = entityState.Transform
            match transform.Rotation with
            | 0.0f ->
                let boundsOverflow = v4BoundsOverflow transform.Position transform.Size entityState.Overflow
                boundsOverflow // no need to transform when unrotated
            | _ ->
                let boundsOverflow = v4BoundsOverflow transform.Position transform.Size entityState.Overflow
                let position = boundsOverflow.Position
                let size = boundsOverflow.Size
                let center = position + size * 0.5f
                let corner = position + size
                let centerToCorner = corner - center
                let quaternion = Quaternion.CreateFromAxisAngle (Vector3.UnitZ, Constants.Math.DegreesToRadiansF * 45.0f)
                let newSizeOver2 = v2Dup (Vector2.Transform (centerToCorner, quaternion)).Y
                let newPosition = center - newSizeOver2
                let newSize = newSizeOver2 * 2.0f
                v4Bounds newPosition newSize

        static member internal getEntityImperative entity world =
            (World.getEntityState entity world).Imperative

        static member internal setEntityImperative value entity world =
            World.updateEntityState
                (fun entityState ->
                    if value <> entityState.Imperative then
                        if value then
                            let properties = UMap.makeFromSeq Imperative (Xtension.toSeq entityState.Xtension)
                            let xtension = Xtension.make properties false true true
                            entityState.Xtension <- xtension
                            entityState.Imperative <- true
                            Some entityState
                        else
                            let properties = UMap.makeFromSeq Functional (Xtension.toSeq entityState.Xtension)
                            let xtension = Xtension.make properties false true false
                            let entityState = { entityState with Xtension = xtension }
                            entityState.Imperative <- false
                            Some entityState
                    else None)
                false Property? Imperative value entity world

        static member internal getEntityModelProperty entity world =
            (World.getEntityState entity world).Model

        static member internal getEntityModel<'a> entity world =
            (World.getEntityState entity world).Model.DesignerValue :?> 'a

        static member internal setEntityModelProperty (value : DesignerProperty) entity world =
            World.updateEntityState
                (fun entityState ->
                    if value.DesignerValue =/= entityState.Model.DesignerValue then
                        if entityState.Imperative then
                            entityState.Model.DesignerValue <- value.DesignerValue
                            Some entityState
                        else Some { entityState with Model = { entityState.Model with DesignerValue = value.DesignerValue }}
                    else None)
                true Property? Model value.DesignerValue entity world

        static member internal setEntityModel<'a> (value : 'a) entity world =
            World.updateEntityState
                (fun entityState ->
                    let valueObj = value :> obj
                    if valueObj =/= entityState.Model.DesignerValue then
                        if entityState.Imperative then
                            entityState.Model.DesignerValue <- valueObj
                            Some entityState
                        else Some { entityState with Model = { DesignerType = typeof<'a>; DesignerValue = valueObj }}
                    else None)
                true Property? Model value entity world

        // NOTE: wouldn't macros be nice?
        static member internal getEntityDispatcher entity world = (World.getEntityState entity world).Dispatcher
        static member internal getEntityFacets entity world = (World.getEntityState entity world).Facets
        static member internal getEntityPosition entity world = (World.getEntityState entity world).Transform.Position
        static member internal getEntitySize entity world = (World.getEntityState entity world).Transform.Size
        static member internal getEntityRotation entity world = (World.getEntityState entity world).Transform.Rotation
        static member internal getEntityElevation entity world = (World.getEntityState entity world).Transform.Elevation
        static member internal getEntityFlags entity world = (World.getEntityState entity world).Transform.Flags
        static member internal getEntityOmnipresent entity world = (World.getEntityState entity world).Omnipresent
        static member internal getEntityAbsolute entity world = (World.getEntityState entity world).Absolute
        static member internal getEntityPublishChanges entity world = (World.getEntityState entity world).PublishChanges
        static member internal getEntityEnabled entity world = (World.getEntityState entity world).Enabled
        static member internal getEntityVisible entity world = (World.getEntityState entity world).Visible
        static member internal getEntityAlwaysUpdate entity world = (World.getEntityState entity world).AlwaysUpdate
        static member internal getEntityPublishUpdates entity world = (World.getEntityState entity world).PublishUpdates
        static member internal getEntityPublishPostUpdates entity world = (World.getEntityState entity world).PublishPostUpdates
        static member internal getEntityPersistent entity world = (World.getEntityState entity world).Persistent
        static member internal getEntityDestroying (entity : Entity) world = List.exists ((=) (entity :> Simulant)) world.DestructionListRev
        static member internal getEntityOptimized entity world = (World.getEntityState entity world).Optimized
        static member internal getEntityOverflow entity world = (World.getEntityState entity world).Overflow
        static member internal getEntityOverlayNameOpt entity world = (World.getEntityState entity world).OverlayNameOpt
        static member internal getEntityFacetNames entity world = (World.getEntityState entity world).FacetNames
        static member internal getEntityScriptFrame entity world = (World.getEntityState entity world).ScriptFrame
        static member internal getEntityCreationTimeStamp entity world = (World.getEntityState entity world).CreationTimeStamp
        static member internal getEntityName entity world = (World.getEntityState entity world).Name
        static member internal getEntityId entity world = (World.getEntityState entity world).Id
        static member internal setEntityPosition value entity world = let mutable transform = &(World.getEntityState entity world).Transform in if v2Neq value transform.Position then World.setEntityTransform { transform with Transform.Position = value } entity world else world
        static member internal setEntitySize value entity world = let mutable transform = &(World.getEntityState entity world).Transform in if v2Neq value transform.Size then World.setEntityTransform { transform with Size = value } entity world else world
        static member internal setEntityRotation value entity world = let mutable transform = &(World.getEntityState entity world).Transform in if value <> transform.Rotation then World.setEntityTransform { transform with Rotation = value } entity world else world
        static member internal setEntityElevation value entity world = let mutable transform = &(World.getEntityState entity world).Transform in if value <> transform.Elevation then World.setEntityTransform { transform with Elevation = value } entity world else world
        static member internal setEntityOmnipresent value entity world = World.updateEntityStatePlus (fun entityState -> if value <> entityState.Omnipresent then Some (if entityState.Imperative then entityState.Omnipresent <- value; entityState else (let entityState = EntityState.copy entityState in entityState.Omnipresent <- value; entityState)) else None) false Property? Omnipresent value entity world
        static member internal setEntityAbsolute value entity world = World.updateEntityStatePlus (fun entityState -> if value <> entityState.Absolute then Some (if entityState.Imperative then entityState.Absolute <- value; entityState else (let entityState = EntityState.copy entityState in entityState.Absolute <- value; entityState)) else None) false Property? Absolute value entity world
        static member internal setEntityPublishChanges value entity world = World.updateEntityState (fun entityState -> if value <> entityState.PublishChanges then Some (if entityState.Imperative then entityState.PublishChanges <- value; entityState else (let entityState = EntityState.copy entityState in entityState.PublishChanges <- value; entityState)) else None) false Property? PublishChanges value entity world
        static member internal setEntityEnabled value entity world = World.updateEntityState (fun entityState -> if value <> entityState.Enabled then Some (if entityState.Imperative then entityState.Enabled <- value; entityState else (let entityState = EntityState.copy entityState in entityState.Enabled <- value; entityState)) else None) false Property? Enabled value entity world
        static member internal setEntityVisible value entity world = World.updateEntityState (fun entityState -> if value <> entityState.Visible then Some (if entityState.Imperative then entityState.Visible <- value; entityState else (let entityState = EntityState.copy entityState in entityState.Visible <- value; entityState)) else None) false Property? Visible value entity world
        static member internal setEntityAlwaysUpdate value entity world = World.updateEntityStatePlus (fun entityState -> if value <> entityState.AlwaysUpdate then Some (if entityState.Imperative then entityState.AlwaysUpdate <- value; entityState else (let entityState = EntityState.copy entityState in entityState.AlwaysUpdate <- value; entityState)) else None) false Property? AlwaysUpdate value entity world
        static member internal setEntityPublishUpdates value entity world = World.updateEntityState (fun entityState -> if value <> entityState.PublishUpdates then Some (if entityState.Imperative then entityState.PublishUpdates <- value; entityState else (let entityState = EntityState.copy entityState in entityState.PublishUpdates <- value; entityState)) else None) false Property? PublishUpdates value entity world
        static member internal setEntityPublishPostUpdates value entity world = World.updateEntityState (fun entityState -> if value <> entityState.PublishPostUpdates then Some (if entityState.Imperative then entityState.PublishPostUpdates <- value; entityState else (let entityState = EntityState.copy entityState in entityState.PublishPostUpdates <- value; entityState)) else None) false Property? PublishPostUpdates value entity world
        static member internal setEntityPersistent value entity world = World.updateEntityState (fun entityState -> if value <> entityState.Persistent then Some (if entityState.Imperative then entityState.Persistent <- value; entityState else (let entityState = EntityState.copy entityState in entityState.Persistent <- value; entityState)) else None) false Property? Persistent value entity world
        static member internal setEntityOverflow value entity world = World.updateEntityStatePlus (fun entityState -> if v2Neq value entityState.Overflow then Some (if entityState.Imperative then entityState.Overflow <- value; entityState else { entityState with Overflow = value }) else None) false Property? Overflow value entity world
        static member internal setEntityScriptFrame value entity world = World.updateEntityState (fun entityState -> if value <> entityState.ScriptFrame then Some (if entityState.Imperative then entityState.ScriptFrame <- value; entityState else { entityState with ScriptFrame = value }) else None) false Property? ScriptFrame value entity world

        static member internal setEntityTransformWithoutEvent value entity world =
            let oldWorld = world
            let oldEntityState = World.getEntityState entity world
            let oldOmnipresent = oldEntityState.Omnipresent
            let oldAbsolute = oldEntityState.Absolute
            let oldBoundsMax = if not oldEntityState.Omnipresent then World.getEntityStateBoundsMax oldEntityState else v4Zero
            let (changed, world) =
                World.updateEntityStateWithoutEvent
                    (fun entityState ->
                        if not (Transform.equals value entityState.Transform)
                        then Some (EntityState.setTransform value entityState)
                        else None)
                    entity world
            if changed then
                if oldEntityState.Transform.Flags <> value.Flags then failwith "Cannot change transform flags via setEntityTransformEithoutEvent."
                World.updateEntityInEntityTree oldOmnipresent oldAbsolute oldBoundsMax entity oldWorld world
            else world

        static member internal setEntityTransform value entity world =
            let oldWorld = world
            let oldEntityState = World.getEntityState entity world
            let oldOmnipresent = oldEntityState.Omnipresent
            let oldAbsolute = oldEntityState.Absolute
            let oldBoundsMax = if not oldEntityState.Omnipresent then World.getEntityStateBoundsMax oldEntityState else v4Zero
            let oldTransform = { oldEntityState.Transform with Position = oldEntityState.Transform.Position }
            let (changed, world) =
                World.updateEntityStateWithoutEvent
                    (fun entityState ->
                        if not (Transform.equals value entityState.Transform)
                        then Some (EntityState.setTransform value entityState)
                        else None)
                    entity world
            if changed then
#if DEBUG
                if oldEntityState.Transform.Flags <> value.Flags then failwith "Cannot change transform flags via setEntityTransform."
#endif
                let world = World.updateEntityInEntityTree oldOmnipresent oldAbsolute oldBoundsMax entity oldWorld world
                if World.getEntityPublishChanges entity world then
                    let positionChanged = v2Neq value.Position oldTransform.Position
                    let sizeChanged = v2Neq value.Size oldTransform.Size
                    let rotationChanged = value.Rotation <> oldTransform.Rotation
                    let elevationChanged = value.Elevation <> oldTransform.Elevation
                    let world = World.publishEntityChange Property? Transform value entity world
                    let world = if positionChanged || sizeChanged then World.publishEntityChange Property? Bounds (v4Bounds value.Position value.Size) entity world else world
                    let world = if positionChanged then World.publishEntityChange Property? Position value.Position entity world else world
                    let world = if positionChanged || sizeChanged then World.publishEntityChange Property? Center (value.Position + value.Size * 0.5f) entity world else world
                    let world = if positionChanged || sizeChanged then World.publishEntityChange Property? Bottom (value.Position + value.Size.WithY 0.0f * 0.5f) entity world else world
                    let world = if sizeChanged then World.publishEntityChange Property? Size value.Size entity world else world
                    let world = if rotationChanged then World.publishEntityChange Property? Rotation value.Rotation entity world else world
                    let world = if elevationChanged then World.publishEntityChange Property? Elevation value.Elevation entity world else world
                    world
                else world
            else world

        static member internal getEntityBounds entity world =
            let mutable transform = &(World.getEntityState entity world).Transform
            v4Bounds transform.Position transform.Size

        static member internal setEntityBounds (value : Vector4) entity world =
            let mutable transform = &(World.getEntityState entity world).Transform
            let position = value.Position
            let size = value.Size
            if v2Neq transform.Position position || v2Neq transform.Size size
            then World.setEntityTransform { transform with Position = position; Size = size } entity world
            else world

        static member internal getEntityCenter entity world =
            let mutable transform = &(World.getEntityState entity world).Transform
            transform.Position + transform.Size * 0.5f

        static member internal setEntityCenter value entity world =
            let mutable transform = &(World.getEntityState entity world).Transform
            let position = value - transform.Size * 0.5f
            if v2Neq transform.Position position
            then World.setEntityTransform { transform with Position = position } entity world
            else world

        static member internal getEntityBottom entity world =
            let mutable transform = &(World.getEntityState entity world).Transform
            transform.Position + transform.Size.WithY 0.0f * 0.5f

        static member internal setEntityBottom value entity world =
            let mutable transform = &(World.getEntityState entity world).Transform
            let position = value - transform.Size.WithY 0.0f * 0.5f
            if v2Neq transform.Position position
            then World.setEntityTransform { transform with Position = position } entity world
            else world

        static member private tryGetFacet facetName world =
            let facets = World.getFacets world
            match Map.tryFind facetName facets with
            | Some facet -> Right facet
            | None -> Left ("Invalid facet name '" + facetName + "'.")

        static member private isFacetCompatibleWithEntity entityDispatcherMap facet (entityState : EntityState) =
            // Note a facet is incompatible with any other facet if it contains any properties that has
            // the same name but a different type.
            let facetType = facet.GetType ()
            let facetPropertyDefinitions = Reflection.getPropertyDefinitions facetType
            if Reflection.isFacetCompatibleWithDispatcher entityDispatcherMap facet entityState then
                List.notExists
                    (fun (propertyDefinition : PropertyDefinition) ->
                        match Xtension.tryGetProperty propertyDefinition.PropertyName entityState.Xtension with
                        | Some property -> property.PropertyType <> propertyDefinition.PropertyType
                        | None -> false)
                    facetPropertyDefinitions
            else false

        static member private getEntityPropertyDefinitionNamesToDetach entityState facetToRemove =

            // get the property definition name counts of the current, complete entity
            let propertyDefinitions = Reflection.getReflectivePropertyDefinitionMap entityState
            let propertyDefinitionNameCounts = Reflection.getPropertyNameCounts propertyDefinitions

            // get the property definition name counts of the facet to remove
            let facetType = facetToRemove.GetType ()
            let facetPropertyDefinitions = Map.singleton facetType.Name (Reflection.getPropertyDefinitions facetType)
            let facetPropertyDefinitionNameCounts = Reflection.getPropertyNameCounts facetPropertyDefinitions

            // compute the difference of the counts
            let finalPropertyDefinitionNameCounts =
                Map.map
                    (fun propertyName propertyCount ->
                        match Map.tryFind propertyName facetPropertyDefinitionNameCounts with
                        | Some facetPropertyCount -> propertyCount - facetPropertyCount
                        | None -> propertyCount)
                    propertyDefinitionNameCounts

            // build a set of all property names where the final counts are negative
            Map.fold
                (fun propertyNamesToDetach propertyName propertyCount ->
                    if propertyCount = 0
                    then Set.add propertyName propertyNamesToDetach
                    else propertyNamesToDetach)
                Set.empty
                finalPropertyDefinitionNameCounts

        /// Get an entity's intrinsic facet names.
        static member getEntityIntrinsicFacetNames entityState =
            let intrinsicFacetNames = entityState.Dispatcher |> getType |> Reflection.getIntrinsicFacetNames
            Set.ofList intrinsicFacetNames

        /// Get an entity's facet names via reflection.
        static member getEntityFacetNamesReflectively (entityState : EntityState) =
            let facetNames = Array.map getTypeName entityState.Facets
            Set.ofArray facetNames

        static member private tryRemoveFacet facetName (entityState : EntityState) entityOpt world =
            match Array.tryFind (fun facet -> getTypeName facet = facetName) entityState.Facets with
            | Some facet ->
                let (entityState, world) =
                    match entityOpt with
                    | Some entity ->
                        let world = World.setEntityState entityState entity world
                        let world = facet.Register (entity, world)
                        let world =
                            if WorldModule.isSelected entity world
                            then facet.UnregisterPhysics (entity, world)
                            else world
                        let entityState = World.getEntityState entity world
                        (entityState, world)
                    | None -> (entityState, world)
                let propertyNames = World.getEntityPropertyDefinitionNamesToDetach entityState facet
                let entityState = Reflection.detachPropertiesViaNames EntityState.copy propertyNames entityState
                let entityState =
                    let facetNames = Set.remove facetName entityState.FacetNames
                    let facets = Array.remove ((=) facet) entityState.Facets
                    if entityState.Imperative then
                        entityState.FacetNames <- facetNames
                        entityState.Facets <- facets
                        entityState
                    else { entityState with FacetNames = facetNames; Facets = facets }
                match entityOpt with
                | Some entity ->
                    let oldWorld = world
                    let oldEntityState = entityState
                    let oldOmnipresent = oldEntityState.Omnipresent
                    let oldAbsolute = oldEntityState.Absolute
                    let oldBoundsMax = if not oldEntityState.Omnipresent then World.getEntityStateBoundsMax oldEntityState else v4Zero
                    let world = World.setEntityState entityState entity world
                    let world = World.updateEntityInEntityTree oldOmnipresent oldAbsolute oldBoundsMax entity oldWorld world
                    Right (World.getEntityState entity world, world)
                | None -> Right (entityState, world)
            | None -> let _ = World.choose world in Left ("Failure to remove facet '" + facetName + "' from entity.")

        static member private tryAddFacet facetName (entityState : EntityState) entityOpt world =
            match World.tryGetFacet facetName world with
            | Right facet ->
                let entityDispatchers = World.getEntityDispatchers world
                if World.isFacetCompatibleWithEntity entityDispatchers facet entityState then
                    let entityState =
                        let facetNames = Set.add facetName entityState.FacetNames
                        let facets = Array.add facet entityState.Facets
                        if entityState.Imperative then
                            entityState.FacetNames <- facetNames
                            entityState.Facets <- facets
                            entityState
                        else { entityState with FacetNames = facetNames; Facets = facets }
                    let entityState = Reflection.attachProperties EntityState.copy facet entityState world
                    match entityOpt with
                    | Some entity ->
                        let oldWorld = world
                        let oldEntityState = entityState
                        let oldOmnipresent = oldEntityState.Omnipresent
                        let oldAbsolute = oldEntityState.Absolute
                        let oldBoundsMax = if not oldEntityState.Omnipresent then World.getEntityStateBoundsMax oldEntityState else v4Zero
                        let world = World.setEntityState entityState entity world
                        let world = World.updateEntityInEntityTree oldOmnipresent oldAbsolute oldBoundsMax entity oldWorld world
                        let world = facet.Register (entity, world)
                        let world =
                            if WorldModule.isSelected entity world
                            then facet.RegisterPhysics (entity, world)
                            else world
                        Right (World.getEntityState entity world, world)
                    | None -> Right (entityState, world)
                else let _ = World.choose world in Left ("Facet '" + getTypeName facet + "' is incompatible with entity '" + scstring entityState.Name + "'.")
            | Left error -> Left error

        static member private tryRemoveFacets facetNamesToRemove entityState entityOpt world =
            Set.fold
                (fun eitherEntityWorld facetName ->
                    match eitherEntityWorld with
                    | Right (entityState, world) -> World.tryRemoveFacet facetName entityState entityOpt world
                    | Left _ as left -> left)
                (Right (entityState, world))
                facetNamesToRemove

        static member private tryAddFacets facetNamesToAdd entityState entityOpt world =
            Set.fold
                (fun eitherEntityStateWorld facetName ->
                    match eitherEntityStateWorld with
                    | Right (entityState, world) -> World.tryAddFacet facetName entityState entityOpt world
                    | Left _ as left -> left)
                (Right (entityState, world))
                facetNamesToAdd

        static member private updateEntityPublishEventFlag setFlag entity eventAddress world =
            let publishUpdates =
                match UMap.tryFind eventAddress (World.getSubscriptions world) with
                | Some subscriptions ->
                    if OMap.isEmpty subscriptions
                    then failwithumf () // NOTE: event system is defined to clean up all empty subscription entries
                    else true
                | None -> false
            if World.getEntityExists entity world
            then setFlag publishUpdates entity world
            else world

        static member internal trySetFacetNames facetNames entityState entityOpt world =
            let intrinsicFacetNames = World.getEntityIntrinsicFacetNames entityState
            let extrinsicFacetNames = Set.fold (flip Set.remove) facetNames intrinsicFacetNames
            let facetNamesToRemove = Set.difference entityState.FacetNames extrinsicFacetNames
            let facetNamesToAdd = Set.difference extrinsicFacetNames entityState.FacetNames
            match World.tryRemoveFacets facetNamesToRemove entityState entityOpt world with
            | Right (entityState, world) -> World.tryAddFacets facetNamesToAdd entityState entityOpt world
            | Left _ as left -> left

        static member internal trySynchronizeFacetsToNames oldFacetNames entityState entityOpt world =
            let facetNamesToRemove = Set.difference oldFacetNames entityState.FacetNames
            let facetNamesToAdd = Set.difference entityState.FacetNames oldFacetNames
            match World.tryRemoveFacets facetNamesToRemove entityState entityOpt world with
            | Right (entityState, world) -> World.tryAddFacets facetNamesToAdd entityState entityOpt world
            | Left _ as left -> left

        static member internal attachIntrinsicFacetsViaNames entityState world =
            let entityDispatchers = World.getEntityDispatchers world
            let facets = World.getFacets world
            Reflection.attachIntrinsicFacets EntityState.copy entityDispatchers facets entityState.Dispatcher entityState world

        static member internal applyEntityOverlay oldOverlayer overlayer world entity =
            let entityState = World.getEntityState entity world
            match entityState.OverlayNameOpt with
            | Some overlayName ->
                let oldFacetNames = entityState.FacetNames
                let entityState = Overlayer.applyOverlayToFacetNames EntityState.copy overlayName overlayName entityState oldOverlayer overlayer
                match World.trySynchronizeFacetsToNames oldFacetNames entityState (Some entity) world with
                | Right (entityState, world) ->
                    let oldWorld = world
                    let oldEntityState = entityState
                    let oldOmnipresent = oldEntityState.Omnipresent
                    let oldAbsolute = oldEntityState.Absolute
                    let oldBoundsMax = if not oldEntityState.Omnipresent then World.getEntityStateBoundsMax oldEntityState else v4Zero
                    let facetNames = World.getEntityFacetNamesReflectively entityState
                    let entityState = Overlayer.applyOverlay6 EntityState.copy overlayName overlayName facetNames entityState oldOverlayer overlayer
                    let world = World.setEntityState entityState entity world
                    World.updateEntityInEntityTree oldOmnipresent oldAbsolute oldBoundsMax entity oldWorld world
                | Left error -> Log.info ("There was an issue in applying a reloaded overlay: " + error); world
            | None -> world

        static member internal tryGetEntityProperty propertyName entity world =
            match World.getEntityStateOpt entity world with
            | Some entityState ->
                match EntityState.tryGetProperty propertyName entityState with
                | Some property as some ->
                    match property.PropertyValue with
                    | :? ComputedProperty as cp -> Some { PropertyType = cp.ComputedType; PropertyValue = cp.ComputedGet (entity :> obj) (world :> obj) }
                    | :? DesignerProperty as dp -> Some { PropertyType = dp.DesignerType; PropertyValue = dp.DesignerValue }
                    | _ -> some
                | None ->
                    match Getters.TryGetValue propertyName with
                    | (true, getter) -> Some (getter entity world)
                    | (false, _) -> None
            | None -> None

        static member internal getEntityProperty propertyName entity world =
            match World.tryGetEntityProperty propertyName entity world with
            | Some property -> property
            | None -> failwithf "Could not find property '%s'." propertyName

        static member internal trySetEntityPropertyWithoutEvent propertyName property entity world =
            match World.getEntityStateOpt entity world with
            | Some entityState ->
                match Setters.TryGetValue propertyName with
                | (false, _) ->
                    let (success, changed, world) =
                        match EntityState.tryGetProperty propertyName entityState with
                        | Some propertyOld ->
                            match propertyOld.PropertyValue with
                            | :? ComputedProperty as cp ->
                                match cp.ComputedSetOpt with
                                | Some computedSet ->
                                    if property.PropertyValue =/= cp.ComputedGet (box entity) (box world)
                                    then (true, true, computedSet property.PropertyValue entity world :?> World)
                                    else (true, false, world)
                                | None -> (false, false, world)
                            | :? DesignerProperty as dp ->
                                if property.PropertyValue =/= dp.DesignerValue then
                                    let property = { property with PropertyValue = { dp with DesignerValue = property.PropertyValue }}
                                    match EntityState.trySetProperty propertyName property entityState with
                                    | (true, entityState) -> (true, true, World.setEntityState entityState entity world)
                                    | (false, _) -> (false, false, world)
                                else (true, false, world)
                            | _ ->
                                if property.PropertyValue =/= propertyOld.PropertyValue then
                                    match EntityState.trySetProperty propertyName property entityState with
                                    | (true, entityState) -> (true, true, World.setEntityState entityState entity world)
                                    | (false, _) -> (false, false, world)
                                else (true, false, world)
                        | None -> (false, false, world)
                    (success, changed, world)
                | (true, setter) ->
                    let (changed, world) = setter property entity world
                    (true, changed, world)
            | None -> (false, false, world)

        static member internal setEntityPropertyWithoutEvent propertyName property entity world =
            match World.trySetEntityPropertyWithoutEvent propertyName property entity world with
            | (true, changed, world) -> (true, changed, world)
            | (false, _, _) -> failwithf "Could not find property '%s'." propertyName

        static member internal trySetEntityProperty propertyName alwaysPublish property entity world =
            match World.trySetEntityPropertyWithoutEvent propertyName property entity world with
            | (true, changed, world) ->
                let world =
                    if changed
                    then World.updateEntityState Some alwaysPublish propertyName property.PropertyValue entity world
                    else world
                (true, world)
            | (false, _, world) -> (false, world)

        static member internal setEntityProperty propertyName alwaysPublish property entity world =
            match World.trySetEntityProperty propertyName alwaysPublish property entity world with
            | (true, world) -> world
            | (false, _) -> failwithf "Could not find property '%s'." propertyName

        static member internal attachEntityProperty propertyName alwaysPublish property entity world =
            if World.getEntityExists entity world then
                World.updateEntityState
                    (fun entityState -> Some (EntityState.attachProperty propertyName property entityState))
                    alwaysPublish propertyName property.PropertyValue entity world
            else failwith ("Cannot attach entity property '" + propertyName + "'; entity '" + entity.Name + "' is not found.")

        static member internal detachEntityProperty propertyName entity world =
            if World.getEntityExists entity world then
                World.updateEntityStateWithoutEvent
                    (fun entityState -> Some (EntityState.detachProperty propertyName entityState))
                    entity world |>
                snd
            else failwith ("Cannot detach entity property '" + propertyName + "'; entity '" + entity.Name + "' is not found.")

        static member internal getEntityDefaultOverlayName dispatcherName world =
            match Option.flatten (World.tryFindRoutedOverlayNameOpt dispatcherName world) with
            | Some _ as opt -> opt
            | None -> Some dispatcherName

        static member internal getEntityBoundsMax entity world =
            let entityState = World.getEntityState entity world
            World.getEntityStateBoundsMax entityState

        static member internal getEntityQuickSize (entity : Entity) world =
            let dispatcher = World.getEntityDispatcher entity world
            let facets = World.getEntityFacets entity world
            let quickSize = dispatcher.GetQuickSize (entity, world)
            Array.fold
                (fun (maxSize : Vector2) (facet : Facet) ->
                    let quickSize = facet.GetQuickSize (entity, world)
                    Vector2
                        (Math.Max (quickSize.X, maxSize.X),
                         Math.Max (quickSize.Y, maxSize.Y)))
                quickSize
                facets

        static member internal getEntitySortingPriority entity world =
            let entityState = World.getEntityState entity world
            { SortElevation = entityState.Transform.Elevation // TODO: P1: see if this should be ElevationLayered
              SortPositionY = entityState.Transform.Position.Y
              SortTarget = entity }

        static member internal updateEntityPublishUpdateFlag entity world =
            World.updateEntityPublishEventFlag World.setEntityPublishUpdates entity (atooa entity.UpdateEventCached) world

#if !DISABLE_ENTITY_POST_UPDATE
        static member internal updateEntityPublishPostUpdateFlag entity world =
            World.updateEntityPublishEventFlag World.setEntityPublishPostUpdates entity (atooa entity.PostUpdateEventCached) world
#endif

        static member internal updateEntityPublishFlags entity world =
            let world = World.updateEntityPublishUpdateFlag entity world
#if !DISABLE_ENTITY_POST_UPDATE
            let world = World.updateEntityPublishPostUpdateFlag entity world
#endif
            world

        static member divergeEntity entity world =
            World.getEntityState entity world |>
            EntityState.copy |>
            flip3 World.setEntityState entity world

        static member internal registerEntity entity world =
            let dispatcher = World.getEntityDispatcher entity world : EntityDispatcher
            let facets = World.getEntityFacets entity world
            let world = dispatcher.Register (entity, world)
            let world =
                Array.fold (fun world (facet : Facet) ->
                    let world = facet.Register (entity, world)
                    if WorldModule.isSelected entity world
                    then facet.RegisterPhysics (entity, world)
                    else world)
                    world facets
            let world = World.updateEntityPublishFlags entity world
            let eventTrace = EventTrace.debug "World" "registerEntity" EventTrace.empty
            let eventAddresses = EventSystemDelegate.getEventAddresses1 (rtoa<unit> [|"Register"; "Event"|] --> entity)
            let world = Array.fold (fun world eventAddress -> World.publish () eventAddress eventTrace entity world) world eventAddresses
            world

        static member internal unregisterEntity (entity : Entity) world =
            let eventTrace = EventTrace.debug "World" "unregisteringEntity" EventTrace.empty
            let eventAddresses = EventSystemDelegate.getEventAddresses1 (rtoa<unit> [|"Unregistering"; "Event"|] --> entity)
            let world = Array.fold (fun world eventAddress -> World.publish () eventAddress eventTrace entity world) world eventAddresses
            let dispatcher = World.getEntityDispatcher entity world : EntityDispatcher
            let facets = World.getEntityFacets entity world
            let world = dispatcher.Unregister (entity, world)
            Array.fold (fun world (facet : Facet) ->
                let world = facet.Unregister (entity, world)
                if WorldModule.isSelected entity world
                then facet.UnregisterPhysics (entity, world)
                else world)
                world facets

        static member internal registerEntityPhysics entity world =
            let facets = World.getEntityFacets entity world
            Array.fold (fun world (facet : Facet) -> facet.RegisterPhysics (entity, world)) world facets

        static member internal unregisterEntityPhysics entity world =
            let facets = World.getEntityFacets entity world
            Array.fold (fun world (facet : Facet) -> facet.UnregisterPhysics (entity, world)) world facets

        static member internal propagateEntityPhysics entity world =
            let world = World.unregisterEntityPhysics entity world
            let world = World.registerEntityPhysics entity world
            world

        static member internal addEntity mayReplace entityState entity world =

            // add entity only if it is new or is explicitly able to be replaced
            let isNew = not (World.getEntityExists entity world)
            if isNew || mayReplace then

                // get old world for entity tree rebuild and change events
                let oldWorld = world
                
                // add entity to world
                let world = World.addEntityState entityState entity world

                // mutate entity tree
                let world =
                    if WorldModule.isSelected entity world then
                        let entityTree =
                            MutantCache.mutateMutant
                                (fun () -> oldWorld.Dispatchers.RebuildEntityTree oldWorld)
                                (fun entityTree ->
                                    let entityState = World.getEntityState entity world
                                    let entityMaxBounds = World.getEntityStateBoundsMax entityState
                                    SpatialTree.addElement (entityState.Omnipresent || entityState.Absolute) entityMaxBounds entity entityTree
                                    entityTree)
                                (World.getEntityTree world)
                        World.setEntityTree entityTree world
                    else world

                // register entity if needed
                if isNew
                then World.registerEntity entity world
                else world

            // handle failure
            else failwith ("Adding an entity that the world already contains at address '" + scstring entity.EntityAddress + "'.")

        /// Destroy an entity in the world immediately. Can be dangerous if existing in-flight publishing depends on
        /// the entity's existence. Consider using World.destroyEntity instead.
        static member destroyEntityImmediate entity world =

            // ensure entity exists in the world
            if World.getEntityExists entity world then

                // remove from destruction
                let world = World.tryRemoveSimulantFromDestruction entity world

                // unregister entity
                let world = World.unregisterEntity entity world

                // get old world for entity tree rebuild
                let oldWorld = world
                
                // mutate entity tree if entity is selected
                let world =
                    if WorldModule.isSelected entity world then
                        let entityTree =
                            MutantCache.mutateMutant
                                (fun () -> oldWorld.Dispatchers.RebuildEntityTree world)
                                (fun entityTree ->
                                    let entityState = World.getEntityState entity oldWorld
                                    let entityMaxBounds = World.getEntityStateBoundsMax entityState
                                    SpatialTree.removeElement (entityState.Omnipresent || entityState.Absolute) entityMaxBounds entity entityTree
                                    entityTree)
                                (World.getEntityTree world)
                        World.setEntityTree entityTree world
                    else world

                // remove cached entity event addresses
                EventSystemDelegate.cleanEventAddressCache entity.EntityAddress
                
                // invalidate entity state
                let entityState = World.getEntityState entity world
                entityState.Invalidated <- true

                // remove the entity from the world
                let world = World.removeEntityState entity world
                world

            // pass
            else world

        /// Create an entity and add it to the world.
        [<FunctionBinding "createEntity">]
        static member createEntity5 dispatcherName nameOpt overlayDescriptor (layer : Layer) world =

            // find the entity's dispatcher
            let dispatchers = World.getEntityDispatchers world
            let dispatcher =
                match Map.tryFind dispatcherName dispatchers with
                | Some dispatcher -> dispatcher
                | None -> failwith ("Could not find an EntityDispatcher named '" + dispatcherName + "'. Did you forget to provide this dispatcher from your NuPlugin?")

            // compute the optional overlay name
            let overlayNameOpt =
                match overlayDescriptor with
                | NoOverlay -> None
                | RoutedOverlay -> Option.flatten (World.tryFindRoutedOverlayNameOpt dispatcherName world)
                | DefaultOverlay -> Some (Option.getOrDefault dispatcherName (Option.flatten (World.tryFindRoutedOverlayNameOpt dispatcherName world)))
                | ExplicitOverlay overlayName -> Some overlayName

            // make the bare entity state (with name as id if none is provided)
            let entityState = EntityState.make nameOpt overlayNameOpt dispatcher

            // attach the entity state's intrinsic facets and their properties
            let entityState = World.attachIntrinsicFacetsViaNames entityState world

            // apply the entity state's overlay to its facet names
            let overlayer = World.getOverlayer world
            let entityState =
                match overlayNameOpt with
                | Some overlayName ->

                    // apply overlay to facets
                    let entityState = Overlayer.applyOverlayToFacetNames EntityState.copy dispatcherName overlayName entityState overlayer overlayer

                    // synchronize the entity's facets (and attach their properties)
                    match World.trySynchronizeFacetsToNames Set.empty entityState None world with
                    | Right (entityState, _) -> entityState
                    | Left error -> Log.debug error; entityState
                | None -> entityState

            // attach the entity state's dispatcher properties
            let entityState = Reflection.attachProperties EntityState.copy entityState.Dispatcher entityState world

            // apply the entity state's overlay if exists
            let entityState =
                match entityState.OverlayNameOpt with
                | Some overlayName ->
                    // OPTIMIZATION: apply overlay only when it will change something
                    if dispatcherName <> overlayName then
                        let facetNames = World.getEntityFacetNamesReflectively entityState
                        Overlayer.applyOverlay EntityState.copy dispatcherName overlayName facetNames entityState overlayer
                    else entityState
                | None -> entityState

            // add entity's state to world
            let entity = Entity (layer.LayerAddress <-- ntoa<Entity> entityState.Name)
            let world =
                if World.getEntityExists entity world then
                    if World.getEntityDestroying entity world
                    then World.destroyEntityImmediate entity world
                    else failwith ("Entity '" + scstring entity + " already exists and cannot be created."); world
                else world
            let world = World.addEntity false entityState entity world

            // HACK: make sure xtension is consistent with imperativeness of entity state
            let world = World.setEntityImperative entityState.Imperative entity world
            (entity, world)

        /// Create an entity from an simulant descriptor.
        static member createEntity4 overlayDescriptor descriptor layer world =
            let (entity, world) =
                World.createEntity5 descriptor.SimulantDispatcherName descriptor.SimulantNameOpt overlayDescriptor layer world
            let world =
                List.fold (fun world (propertyName, property) ->
                    let alwaysPublish = Reflection.isPropertyAlwaysPublishByName propertyName
                    World.setEntityProperty propertyName alwaysPublish property entity world)
                    world descriptor.SimulantProperties
            let world =
                if WorldModule.isSelected entity world
                then World.propagateEntityPhysics entity world
                else world
            (entity, world)

        /// Create an entity and add it to the world.
        static member createEntity<'d when 'd :> EntityDispatcher> nameOpt overlayNameDescriptor layer world =
            World.createEntity5 typeof<'d>.Name nameOpt overlayNameDescriptor layer world

        /// Read an entity from an entity descriptor.
        static member readEntity entityDescriptor nameOpt (layer : Layer) world =

            // make the dispatcher
            let dispatcherName = entityDescriptor.EntityDispatcherName
            let dispatchers = World.getEntityDispatchers world
            let (dispatcherName, dispatcher) =
                match Map.tryFind dispatcherName dispatchers with
                | Some dispatcher -> (dispatcherName, dispatcher)
                | None ->
                    Log.info ("Could not locate dispatcher '" + dispatcherName + "'.")
                    let dispatcherName = typeof<EntityDispatcher>.Name
                    let dispatcher =
                        match Map.tryFind dispatcherName dispatchers with
                        | Some dispatcher -> dispatcher
                        | None -> failwith ("Could not find an EntityDispatcher named '" + dispatcherName + "'. Did you forget to provide this dispatcher from your NuPlugin?")
                    (dispatcherName, dispatcher)

            // get the default overlay name option
            let defaultOverlayNameOpt = World.getEntityDefaultOverlayName dispatcherName world

            // make the bare entity state with name as id
            let entityState = EntityState.make None defaultOverlayNameOpt dispatcher

            // attach the entity state's intrinsic facets and their properties
            let entityState = World.attachIntrinsicFacetsViaNames entityState world

            // read the entity state's overlay and apply it to its facet names if applicable
            let overlayer = World.getOverlayer world
            let entityState = Reflection.tryReadOverlayNameOptToTarget EntityState.copy entityDescriptor.EntityProperties entityState
            let entityState = if Option.isNone entityState.OverlayNameOpt then { entityState with OverlayNameOpt = defaultOverlayNameOpt } else entityState
            let entityState =
                match (defaultOverlayNameOpt, entityState.OverlayNameOpt) with
                | (Some defaultOverlayName, Some overlayName) -> Overlayer.applyOverlayToFacetNames EntityState.copy defaultOverlayName overlayName entityState overlayer overlayer
                | (_, _) -> entityState

            // read the entity state's facet names
            let entityState = Reflection.readFacetNamesToTarget EntityState.copy entityDescriptor.EntityProperties entityState

            // attach the entity state's dispatcher properties
            let entityState = Reflection.attachProperties EntityState.copy entityState.Dispatcher entityState world
            
            // synchronize the entity state's facets (and attach their properties)
            let entityState =
                match World.trySynchronizeFacetsToNames Set.empty entityState None world with
                | Right (entityState, _) -> entityState
                | Left error -> Log.debug error; entityState

            // attempt to apply the entity state's overlay
            let entityState =
                match entityState.OverlayNameOpt with
                | Some overlayName ->
                    // OPTIMIZATION: applying overlay only when it will change something
                    if dispatcherName <> overlayName then
                        let facetNames = World.getEntityFacetNamesReflectively entityState
                        Overlayer.applyOverlay EntityState.copy dispatcherName overlayName facetNames entityState overlayer
                    else entityState
                | None -> entityState

            // read the entity state's values
            let entityState = Reflection.readPropertiesToTarget EntityState.copy entityDescriptor.EntityProperties entityState

            // apply the name if one is provided
            let entityState =
                match nameOpt with
                | Some name -> { entityState with Name = name }
                | None -> entityState

            // try to add entity state to the world
            let entity = Entity (layer.LayerAddress <-- ntoa<Entity> entityState.Name)
            let world =
                if World.getEntityExists entity world then
                    if World.getEntityDestroying entity world
                    then World.destroyEntityImmediate entity world
                    else failwith ("Entity '" + scstring entity + " already exists and cannot be created."); world
                else world
            let world = World.addEntity true entityState entity world

            // HACK: make sure xtension is consistent with imperativeness of entity state
            let world = World.setEntityImperative entityState.Imperative entity world
            (entity, world)

        /// Read an entity from a file.
        [<FunctionBinding>]
        static member readEntityFromFile (filePath : string) nameOpt layer world =
            let entityDescriptorStr = File.ReadAllText filePath
            let entityDescriptor = scvalue<EntityDescriptor> entityDescriptorStr
            World.readEntity entityDescriptor nameOpt layer world

        /// Write an entity to an entity descriptor.
        static member writeEntity (entity : Entity) entityDescriptor world =
            let overlayer = World.getOverlayer world
            let entityState = World.getEntityState entity world
            let entityDispatcherName = getTypeName entityState.Dispatcher
            let entityDescriptor = { entityDescriptor with EntityDispatcherName = entityDispatcherName }
            let entityFacetNames = World.getEntityFacetNamesReflectively entityState
            let overlaySymbolsOpt =
                match entityState.OverlayNameOpt with
                | Some overlayName -> Some (Overlayer.getOverlaySymbols overlayName entityFacetNames overlayer)
                | None -> None
            let shouldWriteProperty = fun propertyName propertyType (propertyValue : obj) ->
                if propertyName = "OverlayNameOpt" && propertyType = typeof<string option> then
                    let defaultOverlayNameOpt = World.getEntityDefaultOverlayName entityDispatcherName world
                    defaultOverlayNameOpt <> (propertyValue :?> string option)
                else
                    match overlaySymbolsOpt with
                    | Some overlaySymbols -> Overlayer.shouldPropertySerialize propertyName propertyType entityState overlaySymbols
                    | None -> true
            let getEntityProperties = Reflection.writePropertiesFromTarget shouldWriteProperty entityDescriptor.EntityProperties entityState
            { entityDescriptor with EntityProperties = getEntityProperties }

        /// Reassign an entity's identity and / or layer. Note that since this destroys the reassigned entity
        /// immediately, you should not call this inside an event handler that involves the reassigned entity itself.
        static member reassignEntityImmediate entity nameOpt (layer : Layer) world =
            let entityState = World.getEntityState entity world
            let world = World.destroyEntityImmediate entity world
            let (id, name) = Gen.idAndNameIf nameOpt
            let entityState = { entityState with Id = id; Name = name }
            let transmutedEntity = Entity (layer.LayerAddress <-- ntoa<Entity> name)
            let world = World.addEntity false entityState transmutedEntity world
            (transmutedEntity, world)

        /// Reassign an entity's identity and / or layer.
        [<FunctionBinding>]
        static member reassignEntity entity nameOpt layer world =
            World.frame (World.reassignEntityImmediate entity nameOpt layer >> snd) world

        /// Try to set an entity's optional overlay name.
        static member trySetEntityOverlayNameOpt overlayNameOpt entity world =
            let oldEntityState = World.getEntityState entity world
            let oldOverlayNameOpt = oldEntityState.OverlayNameOpt
            let entityState =
                if oldEntityState.Imperative then
                    oldEntityState.OverlayNameOpt <- overlayNameOpt
                    oldEntityState
                else { oldEntityState with OverlayNameOpt = overlayNameOpt }
            match (oldOverlayNameOpt, overlayNameOpt) with
            | (Some oldOverlayName, Some overlayName) ->
                let overlayer = World.getOverlayer world
                let (entityState, world) =
                    let oldFacetNames = entityState.FacetNames
                    let entityState = Overlayer.applyOverlayToFacetNames EntityState.copy oldOverlayName overlayName entityState overlayer overlayer
                    match World.trySynchronizeFacetsToNames oldFacetNames entityState (Some entity) world with
                    | Right (entityState, world) -> (entityState, world)
                    | Left error -> Log.debug error; (entityState, world)
                let facetNames = World.getEntityFacetNamesReflectively entityState
                let entityState = Overlayer.applyOverlay EntityState.copy oldOverlayName overlayName facetNames entityState overlayer
                let oldWorld = world
                let oldEntityState = entityState
                let oldOmnipresent = oldEntityState.Omnipresent
                let oldAbsolute = oldEntityState.Absolute
                let oldBoundsMax = if not oldEntityState.Omnipresent then World.getEntityStateBoundsMax oldEntityState else v4Zero
                let world = World.setEntityState entityState entity world
                let world = World.updateEntityInEntityTree oldOmnipresent oldAbsolute oldBoundsMax entity oldWorld world
                let world =
                    if World.getEntityPublishChanges entity world
                    then World.publishEntityChanges entity world
                    else world
                (Right (), world)
            | (None, None) ->
                (Right (), world)
            | (_, _) ->
                (Left "Could not set the entity's overlay name because setting an overlay to or from None is currently unimplemented.", world)
            
        /// Try to set the entity's facet names from script.
        [<FunctionBinding "trySetEntityOverlayNameOpt">]
        static member trySetEntityOverlayNameOptFromScript overlayNameOpt entity world =
            match World.trySetEntityOverlayNameOpt overlayNameOpt entity world with
            | (Right _, world) -> world
            | (Left _, world) -> world

        /// Try to set the entity's facet names.
        static member trySetEntityFacetNames facetNames entity world =
            let entityState = World.getEntityState entity world
            match World.trySetFacetNames facetNames entityState (Some entity) world with
            | Right (entityState, world) ->
                let oldWorld = world
                let oldEntityState = entityState
                let oldOmnipresent = oldEntityState.Omnipresent
                let oldAbsolute = oldEntityState.Absolute
                let oldBoundsMax = if not oldEntityState.Omnipresent then World.getEntityStateBoundsMax oldEntityState else v4Zero
                let world = World.setEntityState entityState entity world
                let world = World.updateEntityInEntityTree oldOmnipresent oldAbsolute oldBoundsMax entity oldWorld world
                let world =
                    if World.getEntityPublishChanges entity world
                    then World.publishEntityChanges entity world
                    else world
                (Right (), world)
            | Left error -> (Left error, world)
            
        /// Try to set the entity's facet names from script.
        [<FunctionBinding "trySetEntityFacetNames">]
        static member trySetEntityFacetNamesFromScript facetNames entity world =
            match World.trySetEntityFacetNames facetNames entity world with
            | (Right _, world) -> world
            | (Left _, world) -> world

        /// View all of the properties of an entity.
        static member internal viewEntityProperties entity world =
            let state = World.getEntityState entity world
            let properties = World.getProperties state
            properties |> Array.ofList |> Array.map a_c

        /// Construct a screen reference in an optimized way.
        /// OPTIMIZATION: attempt to avoid constructing a screen address on each call to decrease
        /// address hashing.
        static member internal makeScreenFast (entity : Entity) world =
            match (World.getGameState world).SelectedScreenOpt with
            | Some screen when screen.Name = Array.head (Address.getNames entity.EntityAddress) -> screen
            | Some _ | None ->
                match (World.getGameState world).OmniScreenOpt with
                | Some omniScreen when omniScreen.Name = Array.head (Address.getNames entity.EntityAddress) -> omniScreen
                | Some _ | None -> Screen (Array.head (entity.EntityAddress.Names))

        static member internal updateEntityInEntityTree oldOmnipresent oldAbsolute oldBoundsMax (entity : Entity) oldWorld world =

            // only need to do this when entity is selected
            if WorldModule.isSelected entity world then

                // OPTIMIZATION: work with the entity state directly to avoid function call overheads
                let entityState = World.getEntityState entity world
                let oldOmnipresent = oldOmnipresent || oldAbsolute
                let newOmnipresent = entityState.Omnipresent || entityState.Absolute
                if newOmnipresent <> oldOmnipresent then

                    // remove and add entity in entity tree
                    let entityTree =
                        MutantCache.mutateMutant
                            (fun () -> oldWorld.Dispatchers.RebuildEntityTree oldWorld)
                            (fun entityTree ->
                                let newBoundsMax = World.getEntityStateBoundsMax entityState
                                SpatialTree.removeElement oldOmnipresent oldBoundsMax entity entityTree
                                SpatialTree.addElement newOmnipresent newBoundsMax entity entityTree
                                let entityBoundsMax = World.getEntityStateBoundsMax entityState
                                SpatialTree.updateElement oldBoundsMax entityBoundsMax entity entityTree
                                entityTree)
                            (World.getEntityTree world)
                    World.setEntityTree entityTree world

                // OPTIMIZATION: only update when entity is not omnipresent
                elif not newOmnipresent then

                    // update entity in entity tree
                    let entityTree =
                        MutantCache.mutateMutant
                            (fun () -> oldWorld.Dispatchers.RebuildEntityTree oldWorld)
                            (fun entityTree ->
                                let entityBoundsMax = World.getEntityStateBoundsMax entityState
                                SpatialTree.updateElement oldBoundsMax entityBoundsMax entity entityTree
                                entityTree)
                            (World.getEntityTree world)
                    World.setEntityTree entityTree world

                // just world
                else world
            else world

        /// Copy an entity to the clipboard.
        static member copyEntityToClipboard entity world =
            let entityState = World.getEntityState entity world
            Clipboard <- Some (entityState :> obj)

        /// Cut an entity to the clipboard.
        static member cutEntityToClipboard entity world =
            World.copyEntityToClipboard entity world
            World.destroyEntityImmediate entity world

        /// Paste an entity from the clipboard.
        static member pasteEntityFromClipboard atMouse rightClickPosition positionSnap rotationSnap (layer : Layer) world =
            match Clipboard with
            | Some entityStateObj ->
                let entityState = entityStateObj :?> EntityState
                let id = Gen.id
                let name = Gen.name
                let entityState = { entityState with Id = id; Name = name }
                let position =
                    if atMouse
                    then World.mouseToWorld entityState.Absolute rightClickPosition world
                    else World.mouseToWorld entityState.Absolute (World.getEyeSize world * 0.5f) world
                let transform = { EntityState.getTransform entityState with Position = position }
                let transform = Math.snapTransform positionSnap rotationSnap transform
                let entityState = EntityState.setTransform transform entityState
                let entity = Entity (layer.LayerAddress <-- ntoa<Entity> name)
                let world = World.addEntity false entityState entity world
                (Some entity, world)
            | None -> (None, world)

    /// Initialize property getters.
    let private initGetters () =
        Getters.Add ("Dispatcher", fun entity world -> { PropertyType = typeof<EntityDispatcher>; PropertyValue = World.getEntityDispatcher entity world })
        Getters.Add ("Facets", fun entity world -> { PropertyType = typeof<Facet array>; PropertyValue = World.getEntityFacets entity world })
        Getters.Add ("PublishChanges", fun entity world -> { PropertyType = typeof<bool>; PropertyValue = World.getEntityPublishChanges entity world })
        Getters.Add ("Imperative", fun entity world -> { PropertyType = typeof<bool>; PropertyValue = World.getEntityImperative entity world })
        Getters.Add ("Transform", fun entity world -> { PropertyType = typeof<Transform>; PropertyValue = (World.getEntityState entity world).Transform })
        Getters.Add ("Bounds", fun entity world -> { PropertyType = typeof<Vector4>; PropertyValue = World.getEntityBounds entity world })
        Getters.Add ("Position", fun entity world -> { PropertyType = typeof<Vector2>; PropertyValue = World.getEntityPosition entity world })
        Getters.Add ("Center", fun entity world -> { PropertyType = typeof<Vector2>; PropertyValue = World.getEntityCenter entity world })
        Getters.Add ("Bottom", fun entity world -> { PropertyType = typeof<Vector2>; PropertyValue = World.getEntityBottom entity world })
        Getters.Add ("Size", fun entity world -> { PropertyType = typeof<Vector2>; PropertyValue = World.getEntitySize entity world })
        Getters.Add ("Rotation", fun entity world -> { PropertyType = typeof<single>; PropertyValue = World.getEntityRotation entity world })
        Getters.Add ("Elevation", fun entity world -> { PropertyType = typeof<single>; PropertyValue = World.getEntityElevation entity world })
        Getters.Add ("Omnipresent", fun entity world -> { PropertyType = typeof<bool>; PropertyValue = World.getEntityOmnipresent entity world })
        Getters.Add ("Absolute", fun entity world -> { PropertyType = typeof<bool>; PropertyValue = World.getEntityAbsolute entity world })
        Getters.Add ("Model", fun entity world -> let designerProperty = World.getEntityModelProperty entity world in { PropertyType = designerProperty.DesignerType; PropertyValue = designerProperty.DesignerValue })
        Getters.Add ("Overflow", fun entity world -> { PropertyType = typeof<Vector2>; PropertyValue = World.getEntityOverflow entity world })
        Getters.Add ("Visible", fun entity world -> { PropertyType = typeof<bool>; PropertyValue = World.getEntityVisible entity world })
        Getters.Add ("Enabled", fun entity world -> { PropertyType = typeof<bool>; PropertyValue = World.getEntityEnabled entity world })
        Getters.Add ("AlwaysUpdate", fun entity world -> { PropertyType = typeof<bool>; PropertyValue = World.getEntityAlwaysUpdate entity world })
        Getters.Add ("PublishUpdates", fun entity world -> { PropertyType = typeof<bool>; PropertyValue = World.getEntityPublishUpdates entity world })
        Getters.Add ("PublishPostUpdates", fun entity world -> { PropertyType = typeof<bool>; PropertyValue = World.getEntityPublishPostUpdates entity world })
        Getters.Add ("Persistent", fun entity world -> { PropertyType = typeof<bool>; PropertyValue = World.getEntityPersistent entity world })
        Getters.Add ("Destroying", fun entity world -> { PropertyType = typeof<bool>; PropertyValue = World.getEntityDestroying entity world })
        Getters.Add ("Optimized", fun entity world -> { PropertyType = typeof<bool>; PropertyValue = World.getEntityOptimized entity world })
        Getters.Add ("OverlayNameOpt", fun entity world -> { PropertyType = typeof<string option>; PropertyValue = World.getEntityOverlayNameOpt entity world })
        Getters.Add ("FacetNames", fun entity world -> { PropertyType = typeof<string Set>; PropertyValue = World.getEntityFacetNames entity world })
        Getters.Add ("CreationTimeStamp", fun entity world -> { PropertyType = typeof<int64>; PropertyValue = World.getEntityCreationTimeStamp entity world })
        Getters.Add ("Name", fun entity world -> { PropertyType = typeof<string>; PropertyValue = World.getEntityName entity world })
        Getters.Add ("Id", fun entity world -> { PropertyType = typeof<Guid>; PropertyValue = World.getEntityId entity world })

    /// Initialize property setters.
    let private initSetters () =
        Setters.Add ("Dispatcher", fun _ _ world -> (false, world))
        Setters.Add ("Facets", fun _ _ world -> (false, world))
        Setters.Add ("Transform", fun property entity world -> (true, World.setEntityTransform (property.PropertyValue :?> Transform) entity world))
        Setters.Add ("Bounds", fun property entity world -> (true, World.setEntityBounds (property.PropertyValue :?> Vector4) entity world))
        Setters.Add ("Position", fun property entity world -> (true, World.setEntityPosition (property.PropertyValue :?> Vector2) entity world))
        Setters.Add ("Center", fun property entity world -> (true, World.setEntityCenter (property.PropertyValue :?> Vector2) entity world))
        Setters.Add ("Bottom", fun property entity world -> (true, World.setEntityBottom (property.PropertyValue :?> Vector2) entity world))
        Setters.Add ("Size", fun property entity world -> (true, World.setEntitySize (property.PropertyValue :?> Vector2) entity world))
        Setters.Add ("Rotation", fun property entity world -> (true, World.setEntityRotation (property.PropertyValue :?> single) entity world))
        Setters.Add ("Elevation", fun property entity world -> (true, World.setEntityElevation (property.PropertyValue :?> single) entity world))
        Setters.Add ("Omnipresent", fun property entity world -> (true, World.setEntityOmnipresent (property.PropertyValue :?> bool) entity world))
        Setters.Add ("Absolute", fun property entity world -> (true, World.setEntityAbsolute (property.PropertyValue :?> bool) entity world))
        Setters.Add ("Model", fun property entity world -> (true, World.setEntityModelProperty { DesignerType = property.PropertyType; DesignerValue = property.PropertyValue } entity world))
        Setters.Add ("Overflow", fun property entity world -> (true, World.setEntityOverflow (property.PropertyValue :?> Vector2) entity world))
        Setters.Add ("Imperative", fun property entity world -> (true, World.setEntityImperative (property.PropertyValue :?> bool) entity world))
        Setters.Add ("PublishChanges", fun property entity world -> (true, World.setEntityPublishChanges (property.PropertyValue :?> bool) entity world))
        Setters.Add ("Visible", fun property entity world -> (true, World.setEntityVisible (property.PropertyValue :?> bool) entity world))
        Setters.Add ("Enabled", fun property entity world -> (true, World.setEntityEnabled (property.PropertyValue :?> bool) entity world))
        Setters.Add ("AlwaysUpdate", fun property entity world -> (true, World.setEntityAlwaysUpdate (property.PropertyValue :?> bool) entity world))
        Setters.Add ("PublishUpdates", fun _ _ world -> (false, world))
        Setters.Add ("PublishPostUpdates", fun _ _ world -> (false, world))
        Setters.Add ("Persistent", fun property entity world -> (true, World.setEntityPersistent (property.PropertyValue :?> bool) entity world))
        Setters.Add ("OverlayNameOpt", fun _ _ world -> (false, world))
        Setters.Add ("FacetNames", fun _ _ world -> (false, world))
        Setters.Add ("CreationTimeStamp", fun _ _ world -> (false, world))
        Setters.Add ("Id", fun _ _ world -> (false, world))
        Setters.Add ("Name", fun _ _ world -> (false, world))

    /// Initialize getters and setters
    let internal init () =
        initGetters ()
        initSetters ()