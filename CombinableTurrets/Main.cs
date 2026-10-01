using BepInEx;
using RoR2;
using RoR2.Navigation;
using RoR2.Skills;
using R2API;
using UnityEngine;
using UnityEngine.AddressableAssets;
using MonoMod.Cil;
using Mono.Cecil.Cil;

namespace CombinableTurrets
{
    // Dependencies
    [BepInDependency(RecalculateStatsAPI.PluginGUID)]

    // Metadata
    [BepInPlugin("Samuel17.CombinableTurrets", "CombinableTurrets", "1.0.2")]

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
            if (body)
            {
                if (BodyCatalog.GetBodyPrefab(body.bodyIndex) == turretPrefab)
                {
                    gameObject.transform.position = newPosition;
                    return;
                } 
            }

            orig(gameObject, newPosition, delta, newRotation, useRotation);
        }

        // Teleport the Gunner Turret to a nearby node upon finishing
        private void TeleportNearby(On.EntityStates.DroneCombiner.DroneCombinerCombining.orig_UpgradeAndEjectDrone orig, EntityStates.DroneCombiner.DroneCombinerCombining self, CharacterBody drone)
        {
            orig(self, drone);

            if (drone && drone.gameObject)
            {
                if (BodyCatalog.GetBodyPrefab(drone.bodyIndex) == turretPrefab)
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
        }

        // Gunner Turret upgrade bonus
        private void GunnerTurretBonus(CharacterBody body, RecalculateStatsAPI.StatHookEventArgs args)
        {
            if (body && BodyCatalog.GetBodyPrefab(body.bodyIndex) == turretPrefab) {
                if (body.inventory)
                {
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
        }
    }
}
