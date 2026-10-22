using BepInEx;
using RoR2;
using RoR2.Navigation;
using RoR2.Skills;
using R2API;
using UnityEngine;
using UnityEngine.AddressableAssets;
using MonoMod.Cil;
using Mono.Cecil.Cil;
using System;

namespace CombinableTurrets
{
    // Dependencies
    [BepInDependency(RecalculateStatsAPI.PluginGUID)]

    // Metadata
    [BepInPlugin("Samuel17.CombinableTurrets", "CombinableTurrets", "1.0.3")]

    public class Main : BaseUnityPlugin
    {
        // Load addressables
        public static GameObject turretPrefab = Addressables.LoadAssetAsync<GameObject>("RoR2/Base/Drones/Turret1Body.prefab").WaitForCompletion();
        public static GameObject teleportHelperPrefab = Addressables.LoadAssetAsync<GameObject>("RoR2/Base/Common/DirectorSpawnProbeHelperPrefab.prefab").WaitForCompletion();
        public static SkillDef turretSkillDef = Addressables.LoadAssetAsync<SkillDef>("RoR2/Base/Drones/Turret1BodyTurret.asset").WaitForCompletion();

        // Fields
        public static int extraBulletsPerTier = 20;
        public static float extraAtkSpeedMultiplierPerTier = .5f;

        public void Awake()
        {
            // Logging!
            Log.Init(Logger);
            
            RoR2Application.onLoad += () =>
            {
                // Allow Gunner Turret to be combinable
                DroneDef turretDef = DroneCatalog.FindDroneDefFromBody(turretPrefab);
                if (turretDef)
                {
                    turretDef.canCombine = true;
                }

                // Main combining hook
                IL.RoR2.DroneCombinerController.TryGetCombinableDrones += SkipBodyFlag;
            };

            // Secondary combining hooks
            On.RoR2.TeleportHelper.OnTeleport_GameObject_Vector3_Vector3_Quaternion_bool += TurretException;
            On.EntityStates.DroneCombiner.DroneCombinerCombining.UpgradeAndEjectDrone += TeleportNearby;

            // Broken turret fix
            On.EntityStates.Turret1.DeathState.OnEnter += TrackUpgrade;
            IL.EntityStates.Turret1.DeathState.OnExit += KeepUpgradeOnDeath;
            CharacterBody.onBodyStartGlobal += ScaleFix;

            // Turret upgrade bonus
            RecalculateStatsAPI.GetStatCoefficients += GunnerTurretBonus;
        }

        // Drone body flag is no longer required to be combinable
        private void SkipBodyFlag(ILContext il)
        {
            ILCursor c = new(il);

            if (
                !c.TryGotoNext(MoveType.After,
                x => x.MatchLdloc(out _),
                x => x.MatchLdfld<CharacterBody>(nameof(CharacterBody.bodyFlags)),
                x => x.MatchLdcI4((int)CharacterBody.BodyFlags.Drone),
                x => x.MatchAnd()
            ))
            {
                Log.Error("Combinable Gunner Turret hook failed!");
            }

            c.Emit(OpCodes.Pop);
            c.Emit(OpCodes.Ldc_I4_1); // I don't get checking for the Drone body flag, you're already checking for canCombine. Skip that.
        }

        // If combining Gunner Turrets, just don't call this method at all or it'll break
        private void TurretException(On.RoR2.TeleportHelper.orig_OnTeleport_GameObject_Vector3_Vector3_Quaternion_bool orig, GameObject gameObject, Vector3 newPosition, Vector3 delta, Quaternion newRotation, bool useRotation)
        {
            CharacterBody body = gameObject.GetComponent<CharacterBody>();
            if (body && IsGunnerTurret(body))
            {
                gameObject.transform.position = newPosition;
                return;
            }

            orig(gameObject, newPosition, delta, newRotation, useRotation);
        }

        // Teleport the Gunner Turret to a nearby node upon finishing
        private void TeleportNearby(On.EntityStates.DroneCombiner.DroneCombinerCombining.orig_UpgradeAndEjectDrone orig, EntityStates.DroneCombiner.DroneCombinerCombining self, CharacterBody drone)
        {
            orig(self, drone);

            if (drone && drone.gameObject && IsGunnerTurret(drone))
            {
                Vector3 targetPosition = self.transform.position;

                SpawnCard spawnCard = ScriptableObject.CreateInstance<SpawnCard>();
                spawnCard.hullSize = drone.hullClassification;
                spawnCard.nodeGraphType = MapNodeGroup.GraphType.Ground;
                spawnCard.prefab = teleportHelperPrefab;

                GameObject teleportDestinationHelper = DirectorCore.instance.TrySpawnObject(new DirectorSpawnRequest(spawnCard, new DirectorPlacementRule
                {
                    placementMode = DirectorPlacementRule.PlacementMode.Approximate,
                    position = targetPosition,
                    minDistance = 5f,
                    maxDistance = 20f
                }, RoR2Application.rng));

                if (teleportDestinationHelper)
                {
                    Vector3 position = teleportDestinationHelper.transform.position;
                    TeleportHelper.TeleportBody(drone, position, false);
                    GameObject teleportEffectPrefab = Run.instance.GetTeleportEffectPrefab(drone.gameObject);
                    if (teleportEffectPrefab)
                    {
                        EffectManager.SimpleEffect(teleportEffectPrefab, position, Quaternion.identity, true);
                    }
                    Destroy(teleportDestinationHelper);

                    // Orient to floor
                    Transform transform = drone.gameObject.transform;
                    if (transform)
                    {
                        Vector3 up = transform.up;
                        if (Physics.Raycast(new Ray(position + up, -up), out var hitInfo, 2f, LayerIndex.world.mask))
                        {
                            transform.up = hitInfo.normal;
                        }
                    }
                }
            }
        }

        // Track the Gunner Turret's upgrades on death
        private void TrackUpgrade(On.EntityStates.Turret1.DeathState.orig_OnEnter orig, EntityStates.Turret1.DeathState self)
        {
            orig(self);

            if (self.characterBody && self.characterBody.inventory)
            {
                var tracker = self.gameObject.AddComponent<TurretUpgradeTracker>();
                tracker.droneUpgradeCount = self.characterBody.inventory.GetItemCountEffective(DLC3Content.Items.DroneUpgradeHidden);
            }
        }

        // Broken Gunnner Turrets keep their upgrade Tier
        private void KeepUpgradeOnDeath(ILContext il)
        {
            ILCursor c = new(il);
            TurretUpgradeTracker tracker = null;

            if (
                !c.TryGotoNext(MoveType.Before,
                x => x.MatchLdloc(out _),
                x => x.MatchCallOrCallvirt<GameObject>(nameof(GameObject.GetComponent)),
                x => x.MatchStloc(out _)
            ))
            {
                Log.Error("Gunner Turret death hook failed!");
            }

            c.Emit(OpCodes.Ldarg_0);
            c.Emit(OpCodes.Ldloc_S, (byte)3);
            c.EmitDelegate<Action<EntityStates.Turret1.DeathState, GameObject>>((self, spawnedInstance) =>
            {
                SummonMasterBehavior smb = spawnedInstance.GetComponent<SummonMasterBehavior>();
                tracker = self.GetComponent<TurretUpgradeTracker>();
                if (smb && tracker)
                {
                    smb.NetworkdroneUpgradeCount = tracker.droneUpgradeCount;
                }
            });

            // Cost adjustment (additive +20% per tier)
            if (
                !c.TryGotoNext(MoveType.After,
                x => x.MatchLdfld<EntityStates.Turret1.DeathState>(nameof(EntityStates.Turret1.DeathState.deadFormToSpawnOnDeath_CostCoefficient)),
                x => x.MatchMul()
            ) && tracker)
            {
                Log.Error("Gunner Turret cost hook failed!");
            }

            c.EmitDelegate<Func<float>>(() =>
            {
                return 1f + tracker.droneUpgradeCount * .2f;
            });
            c.Emit(OpCodes.Mul);
        }

        // Fix for Gunner Turrets refusing to update their size when repaired
        private void ScaleFix(CharacterBody body)
        {
            if (!body || !body.modelLocator || !IsGunnerTurret(body)) return;

            var upgradeBodyBehavior = body.GetComponent<RoR2.Items.DroneUpgradeHiddenBodyBehavior>();
            if (!upgradeBodyBehavior) return;

            float scaleFromUpgradeCount = DroneUpgradeUtils.GetScaleFromUpgradeCount(upgradeBodyBehavior.previousStack);
            float num = DroneUpgradeUtils.GetScaleFromUpgradeCount(upgradeBodyBehavior.stack) / scaleFromUpgradeCount;
            body.modelLocator.desiredModelScale *= num;
        }

        // Gunner Turret upgrade bonus
        private void GunnerTurretBonus(CharacterBody body, RecalculateStatsAPI.StatHookEventArgs args)
        {
            if (body && body.inventory && IsGunnerTurret(body)) {
                int itemCount = body.inventory.GetItemCountEffective(DLC3Content.Items.DroneUpgradeHidden);
                if (itemCount > 0)
                {
                    args.primarySkill.bonusStockAdd += extraBulletsPerTier * itemCount;
                    args.attackSpeedTotalMult *= 1 + extraAtkSpeedMultiplierPerTier * itemCount;

                    // Recharge all stocks at once, regardless of count
                    if (body.skillLocator && body.skillLocator.primary && body.skillLocator.primary.skillDef && body.skillLocator.primary.skillDef == turretSkillDef)
                    {
                        body.skillLocator.primary.skillDef.rechargeStock = body.skillLocator.primary.maxStock;
                    }
                }
            }
        }

        private bool IsGunnerTurret(CharacterBody body)
        {
            return BodyCatalog.GetBodyPrefab(body.bodyIndex) == turretPrefab;
        }
    }
}
