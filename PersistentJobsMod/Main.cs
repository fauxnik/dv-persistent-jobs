using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityModManagerNet;
using Harmony12;
using System.Reflection;
using DV.Logic.Job;

namespace PersistentJobsMod
{
    static class Main
    {
        private static UnityModManager.ModEntry thisModEntry;
        private static bool isModBroken = false;
        private static float initialDistanceRegular = 0f;
        private static float initialDistanceAnyJobTaken = 0f;

        static void Load(UnityModManager.ModEntry modEntry)
        {
            var harmony = HarmonyInstance.Create(modEntry.Info.Id);
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            modEntry.OnToggle = OnToggle;
            thisModEntry = modEntry;
        }

        static bool OnToggle(UnityModManager.ModEntry modEntry, bool isTogglingOn)
        {
            if (isModBroken)
            {
                return !isTogglingOn;
            }
            return true;
        }

        static void OnCriticalFailure()
        {
            isModBroken = true;
            thisModEntry.Active = false;
        }

        [HarmonyPatch(typeof(StationController), "ExpireAllAvailableJobsInStation")]
        class StationController_ExpireAllAvailableJobsInStation_Patch
        {
            static bool Prefix()
            {
                // skip the original method entirely when this mod is active
                // doing so prevents jobs from expiring due to the player's distance from the station center
                return !thisModEntry.Active;
            }
        }

        [HarmonyPatch(typeof(StationJobGenerationRange))]
        [HarmonyPatchAll]
        class StationJobGenerationRange_AllMethods_Patch
        {
            static void Prefix(StationJobGenerationRange __instance, MethodBase __originalMethod)
            {
                try
                {
                    // backup existing values before overwriting
                    if (initialDistanceRegular < 1f)
                    {
                        initialDistanceRegular = __instance.destroyGeneratedJobsSqrDistanceRegular;
                    }
                    if (initialDistanceAnyJobTaken < 1f)
                    {
                        initialDistanceAnyJobTaken = __instance.destroyGeneratedJobsSqrDistanceAnyJobTaken;
                    }

                    if (thisModEntry.Active)
                    {
                        if (__instance.destroyGeneratedJobsSqrDistanceAnyJobTaken < 4000000f)
                        {
                            __instance.destroyGeneratedJobsSqrDistanceAnyJobTaken = 4000000f;
                        }
                        __instance.destroyGeneratedJobsSqrDistanceRegular = __instance.destroyGeneratedJobsSqrDistanceAnyJobTaken;
                    }
                    else
                    {
                        __instance.destroyGeneratedJobsSqrDistanceRegular = initialDistanceRegular;
                        __instance.destroyGeneratedJobsSqrDistanceAnyJobTaken = initialDistanceAnyJobTaken;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError(string.Format("Exception thrown during StationJobGenerationRange.{0} prefix patch:\n{1}", __originalMethod.Name, e.Message));
                    OnCriticalFailure();
                }
            }
        }

        // expires a job if none of its cars are in range of the starting station on job start attempt
        [HarmonyPatch(typeof(JobValidator), "ProcessJobOverview")]
        class JobValidator_ProcessJobOverview_Patch
        {
            static void Prefix(
                List<StationController> ___allStations,
                DV.Printers.PrinterController ___bookletPrinter,
                JobOverview jobOverview)
            {
                try
                {
                    if (!thisModEntry.Active)
                    {
                        return;
                    }

                    Job job = jobOverview.job;
                    StationController stationController = ___allStations.FirstOrDefault(
                        (StationController st) => st.logicStation.availableJobs.Contains(job)
                    );

                    if (___bookletPrinter.IsOnCooldown || job.State != JobState.Available || stationController == null)
                    {
                        return;
                    }

                    // expire the job if all associated cars are outside the job destruction range
                    // the base method's logic will handle generating the expired report
                    StationJobGenerationRange stationRange = Traverse.Create(stationController)
                        .Field("stationRange")
                        .GetValue<StationJobGenerationRange>();
                    if (!job.tasks.Any(CheckTaskForCarsInRange(stationRange)))
                    {
                        job.ExpireJob();
                    }
                }
                catch (Exception e)
                {
                    thisModEntry.Logger.Error(string.Format(
                        "Exception thrown during JobValidator.ProcessJobOverview prefix patch:\n{0}",
                        e.ToString()
                    ));
                    OnCriticalFailure();
                }
            }

            private static Func<Task, bool> CheckTaskForCarsInRange(StationJobGenerationRange stationRange)
            {
                return (Task t) =>
                {
                    if (t is ParallelTasks || t is SequentialTasks)
                    {
                        return Traverse.Create(t).Field("tasks").GetValue<IEnumerable<Task>>().Any(CheckTaskForCarsInRange(stationRange));
                    }
                    List<Car> cars = Traverse.Create(t).Field("cars").GetValue<List<Car>>();
                    Car carInRangeOfStation = cars.FirstOrDefault((Car c) =>
                    {
                        TrainCar trainCar = TrainCar.GetTrainCarByCarGuid(c.carGuid);
                        float distance =
                            (trainCar.transform.position - stationRange.stationCenterAnchor.position).sqrMagnitude;
                        return trainCar != null && distance <= initialDistanceRegular;
                    });
                    return carInRangeOfStation != null;
                };
            }
        }
    }
}