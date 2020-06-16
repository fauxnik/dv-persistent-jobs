using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityModManagerNet;
using Harmony12;
using System.Reflection;

namespace PersistentJobsMod
{
    static class Main
    {
        static void Load(UnityModManager.ModEntry modEntry)
        {
            var harmony = HarmonyInstance.Create(modEntry.Info.Id);
            harmony.PatchAll(Assembly.GetExecutingAssembly());
        }

        [HarmonyPatch(typeof(StationController), "ExpireAllAvailableJobsInStation")]
        class StationController_ExpireAllAvailableJobsInStation_Patch
        {
            [HarmonyPrefix]
            static bool SkipJobExpiration()
            {
                return false; // skip the original method entirely
            }
        }

        [HarmonyPatch(typeof(StationJobGenerationRange), "Awake")]
        class StationJobGenerationRange_Awake_Patch
        {
            [HarmonyPrefix]
            static void ExpandDestroyJobsSqrDistanceRegular(StationJobGenerationRange __instance)
            {
                if (__instance.destroyGeneratedJobsSqrDistanceAnyJobTaken < 4000000f)
                {
                    __instance.destroyGeneratedJobsSqrDistanceAnyJobTaken = 4000000f;
                }
                __instance.destroyGeneratedJobsSqrDistanceRegular = __instance.destroyGeneratedJobsSqrDistanceAnyJobTaken;
            }
        }
    }
}