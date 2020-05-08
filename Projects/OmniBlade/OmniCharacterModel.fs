﻿namespace OmniBlade
open Prime
open Nu

[<RequireQualifiedAccess>]
module CharacterModel =

    type [<ReferenceEquality; NoComparison>] CharacterModel =
        private
            { CharacterState : CharacterState
              AnimationState : CharacterAnimationState
              InputState_ : CharacterInputState
              BoundsOriginal_ : Vector4
              Bounds_ : Vector4 }

        (* CharacterState Properties *)
        member this.CharacterType = this.CharacterState.CharacterType
        member this.PartyIndex = this.CharacterState.PartyIndex
        member this.ExpPoints = this.CharacterState.ExpPoints
        member this.HitPoints = this.CharacterState.HitPoints
        member this.SpecialPoints = this.CharacterState.SpecialPoints
        member this.WeaponOpt = this.CharacterState.WeaponOpt
        member this.ArmorOpt = this.CharacterState.ArmorOpt
        member this.Accessories = this.CharacterState.Accessories
        member this.Specials = this.CharacterState.Specials
        member this.Statuses = this.CharacterState.Statuses
        member this.Defending = this.CharacterState.Defending
        member this.Charging = this.CharacterState.Charging
        member this.PowerBuff = this.CharacterState.PowerBuff
        member this.ShieldBuff = this.CharacterState.ShieldBuff
        member this.MagicBuff = this.CharacterState.MagicBuff
        member this.CounterBuff = this.CharacterState.CounterBuff
        member this.ActionTime = this.CharacterState.ActionTime
        member this.AutoBattleOpt = this.CharacterState.AutoBattleOpt
        member this.CharacterIndex = this.CharacterState.CharacterIndex
        member this.Name = this.CharacterState.Name
        member this.IsEnemy = this.CharacterState.IsEnemy
        member this.IsAlly = this.CharacterState.IsAlly
        member this.IsHealthy = this.CharacterState.IsHealthy
        member this.IsWounded = this.CharacterState.IsWounded
        member this.Level = this.CharacterState.Level
        member this.HitPointsMax rom = this.CharacterState.HitPointsMax rom
        member this.Power rom = this.CharacterState.Power rom
        member this.Magic rom = this.CharacterState.Magic rom
        member this.Shield rom = this.CharacterState.Shield rom

        (* AnimationState Properties *)
        member this.TimeStart = this.AnimationState.TimeStart
        member this.AnimationSheet = this.AnimationState.AnimationSheet
        member this.AnimationCycle = this.AnimationState.AnimationCycle
        member this.Direction = this.AnimationState.Direction

        (* InputState Properties *)
        member this.InputState = this.InputState_

        (* Bounds Original Properties *)
        member this.BoundsOriginal = this.BoundsOriginal_
        member this.PositionOriginal = this.BoundsOriginal_.Position
        member this.CenterOriginal = this.BoundsOriginal_.Center
        member this.BottomOriginal = this.BoundsOriginal_.Bottom
        member this.SizeOriginal = this.BoundsOriginal_.Size

        (* Bounds Properties *)
        member this.Bounds = this.Bounds_
        member this.Position = this.Bounds_.Position
        member this.Center = this.Bounds_.Center
        member this.Bottom = this.Bounds_.Bottom
        member this.Size = this.Bounds_.Size

        (* Helper Properties *)
        member this.CenterOffset = this.Center + Constants.Battle.CharacterCenterOffset
        member this.CenterOffset2 = this.Center + Constants.Battle.CharacterCenterOffset2
        member this.CenterOffset3 = this.Center + Constants.Battle.CharacterCenterOffset3
        member this.BottomOffset = this.Bottom + Constants.Battle.CharacterBottomOffset
        member this.BottomOffset2 = this.Bottom + Constants.Battle.CharacterBottomOffset2

        static member evaluateAutoBattle source (target : CharacterModel) =
            let specialOpt =
                match Gen.random1 Constants.Battle.AutoBattleSpecialFrequency with
                | 0 -> CharacterState.tryGetSpecialRandom source.CharacterState
                | _ -> None
            { AutoTarget = target.CharacterIndex; AutoSpecialOpt = specialOpt }

        static member evaluateSpecialMove rom specialData source target =
            match specialData.EffectType with
            | Physical ->
                let power = source.CharacterState.Power rom
                if specialData.Curative then
                    let healing = single power * specialData.Scalar |> int |> max 1
                    (false, healing)
                else
                    let cancelled = specialData.Cancels && CharacterState.runningSpecialAutoBattle target.CharacterState
                    let shield = target.CharacterState.Shield rom
                    let damageUnscaled = power - shield
                    let damage = single damageUnscaled * specialData.Scalar |> int |> max 1
                    (cancelled, -damage)
            | Magical ->
                let magic = source.CharacterState.Magic rom
                if specialData.Curative then
                    let healing = single magic * specialData.Scalar |> int |> max 1
                    (false, healing)
                else
                    let cancelled = specialData.Cancels && CharacterState.runningSpecialAutoBattle target.CharacterState
                    let shield = target.CharacterState.Shield rom
                    let damageUnscaled = magic - shield
                    let damage = single damageUnscaled * specialData.Scalar |> int |> max 1
                    (cancelled, -damage)

        static member getPoiseType character =
            CharacterState.getPoiseType character.CharacterState

        static member getAttackResult rom source target =
            CharacterState.getAttackResult rom source.CharacterState target.CharacterState

        static member getAnimationIndex rom time character =
            CharacterAnimationState.index rom time character.AnimationState

        static member getAnimationProgressOpt rom time character =
            CharacterAnimationState.progressOpt rom time character.AnimationState

        static member getAnimationFinished rom time character =
            CharacterAnimationState.getFinished rom time character.AnimationState

        static member runningSpecialAutoBattle character =
            CharacterState.runningSpecialAutoBattle character.CharacterState

        static member isReadyForAutoBattle character =
            Option.isNone character.CharacterState.AutoBattleOpt &&
            character.CharacterState.ActionTime > Constants.Battle.AutoBattleReadyTime &&
            character.CharacterState.IsEnemy

        static member updateHitPoints rom updater character =
            { character with CharacterState = CharacterState.updateHitPoints rom updater character.CharacterState }

        static member updateSpecialPoints rom updater character =
            { character with CharacterState = CharacterState.updateSpecialPoints rom updater character.CharacterState }

        static member updateInputState updater character =
            { character with InputState_ = updater character.InputState_ }
    
        static member updateActionTime updater character =
            { character with CharacterState = CharacterState.updateActionTime updater character.CharacterState }

        static member updateAutoBattleOpt updater character =
            { character with CharacterState = CharacterState.updateAutoBattleOpt updater character.CharacterState }

        static member updateBounds updater (character : CharacterModel) =
            { character with Bounds_ = updater character.Bounds_ }

        static member updatePosition updater (character : CharacterModel) =
            { character with Bounds_ = character.Position |> updater |> character.Bounds.WithPosition }

        static member updateCenter updater (character : CharacterModel) =
            { character with Bounds_ = character.Center |> updater |> character.Bounds.WithCenter }

        static member updateBottom updater (character : CharacterModel) =
            { character with Bounds_ = character.Bottom |> updater |> character.Bounds.WithBottom }

        static member autoBattle (source : CharacterModel) (target : CharacterModel) =
            let sourceToTarget = target.Position - source.Position
            let autoBattle = CharacterModel.evaluateAutoBattle source target
            let characterState = { source.CharacterState with AutoBattleOpt = Some autoBattle }
            let direction = Direction.fromVector2 sourceToTarget
            let animationState = { source.AnimationState with Direction = direction }
            { source with CharacterState = characterState; AnimationState = animationState }

        static member defend character =
            let characterState = character.CharacterState
            let characterState =
                // TODO: shield buff
                if not characterState.Defending
                then { characterState with CounterBuff = max 0.0f (characterState.CounterBuff + Constants.Battle.DefendingCounterBuff) }
                else characterState
            let characterState = { characterState with Defending = true }
            { character with CharacterState = characterState }

        static member undefend character =
            let characterState = character.CharacterState
            let characterState =
                // TODO: shield buff
                if characterState.Defending
                then { characterState with CounterBuff = max 0.0f (characterState.CounterBuff - Constants.Battle.DefendingCounterBuff) }
                else characterState
            let characterState = { characterState with Defending = false }
            { character with CharacterState = characterState }

        static member animate time cycle character =
            { character with AnimationState = CharacterAnimationState.setCycle (Some time) cycle character.AnimationState }

        static member make characterState animationState inputState bounds =
            { CharacterState = characterState
              AnimationState = animationState
              InputState_ = inputState
              BoundsOriginal_ = bounds
              Bounds_ = bounds }

type CharacterModel = CharacterModel.CharacterModel