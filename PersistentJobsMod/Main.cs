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
            thisModEntry.Logger.Critical("Deactivating mod PersistentJobs due to critical failure!");
            thisModEntry.Logger.Warning("You can reactivate PersistentJobs by restarting the game, but this failure " +
                "type likely indicates an incompatibility between the mod and a recent game update. Please search the " +
                "mod's Github issue tracker for a relevant report. If none is found, please open one. Include the" +
                "exception message printed above and your game's current build number.");
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
                    thisModEntry.Logger.Error(string.Format("Exception thrown during StationJobGenerationRange.{0} prefix patch:\n{1}", __originalMethod.Name, e.Message));
                    OnCriticalFailure();
                }
            }
        }

        [HarmonyPatch(typeof(JobValidator), "ProcessJobOverview")]
        class JobValidator_ProcessJobOverview_Patch
        {
            static void Prefix(List<StationController> ___allStations, DV.Printers.PrinterController ___bookletPrinter, JobOverview jobOverview)
            {
                try
                {
                    if (!thisModEntry.Active)
                    {
                        return;
                    }

                    Job job = jobOverview.job;
                    StationController stationController = ___allStations.FirstOrDefault((StationController st) => st.logicStation.availableJobs.Contains(job));

                    if (___bookletPrinter.IsOnCooldown || job.State != JobState.Available || stationController == null)
                    {
                        return;
                    }

                    // expire the job if all associated cars are outside the job destruction range at time of job overview processing
                    // the base method's logic will handle generating the expired report
                    StationJobGenerationRange stationRange = Traverse.Create(stationController).Field("stationRange").GetValue<StationJobGenerationRange>();
                    Task taskWithCarsInRangeOfStation = job.tasks.FirstOrDefault((Task t) =>
                    {
                        List<Car> cars = Traverse.Create(t).Field("cars").GetValue<List<Car>>();
                        Car carInRangeOfStation = cars.FirstOrDefault((Car c) =>
                        {
                            TrainCar trainCar = TrainCar.GetTrainCarByCarGuid(c.carGuid);
                            return trainCar != null && (trainCar.transform.position - stationRange.stationCenterAnchor.position).sqrMagnitude <= initialDistanceRegular;
                        });
                        return carInRangeOfStation != null;
                    });
                    if (taskWithCarsInRangeOfStation == null)
                    {
                        job.ExpireJob();
                    }
                }
                catch (Exception e)
                {
                    thisModEntry.Logger.Error(string.Format("Exception thrown during JobValidator.ProcessJobOverview prefix patch:\n{0}", e.Message));
                    OnCriticalFailure();
                }
            }
        }

        [HarmonyPatch(typeof(JobChainControllerWithEmptyHaulGeneration), "OnLastJobInChainCompleted")]
        class JobChainControllerWithEmptyHaulGeneration_OnLastJobInChainCompleted_Patch
        {
            static void Prefix()
            {
                try
                {
                    // TODO: if possible, generate another TransportJobChain (instead of an EmptyHaulJob)
                }
                catch (Exception e)
                {
                    thisModEntry.Logger.Error(string.Format("Exception thrown during JobChainControllerWithEmptyHaulGeneration.OnLastJobInChainCompleted prefix patch:\n{0}", e.Message));
                    OnCriticalFailure();
                }
            }
        }

        [HarmonyPatch(typeof(JobChainController), "OnLastJobInChainCompleted")]
        class JobChainController_OnLastJobInChainCompleted_Patch
        {
            static void Prefix(Job lastJobInChain, JobChainController __instance, List<StaticJobDefinition> ___jobChain)
            {
                try
                {
                    if (lastJobInChain.jobType != JobType.EmptyHaul)
                    {
                        thisModEntry.Logger.Warning(string.Format("Expected JobType.EmptyHaul but received {0}. Skipping job generation!", lastJobInChain.jobType.ToString()));
                        return;
                    }

                    // generate one or more TransportJobChains
                    // inspired by JobChainControllerWithEmptyHaulGeneration.OnLastJobInChainCompleted
                    StaticEmptyHaulJobDefinition staticEmptyHaulJobDefinition = ___jobChain[___jobChain.Count - 1] as StaticEmptyHaulJobDefinition;
                    if (staticEmptyHaulJobDefinition != null)
                    {
                        StationController startingStation = SingletonBehaviour<LogicController>.Instance.YardIdToStationController[staticEmptyHaulJobDefinition.chainData.chainDestinationYardId];
                        System.Random rng = new System.Random(Environment.TickCount);
                        Track track = staticEmptyHaulJobDefinition.destinationTrack;
                        List<TrainCar> trainCars = Traverse.CreateWithType("JobChainControllerWithEmptyHaulGeneration")
                            .Method("ExtractCorrespondingTrainCars")
                            .GetValue<List<TrainCar>>(staticEmptyHaulJobDefinition.trainCarsToTransport);
                        if (trainCars == null)
                        {
                            thisModEntry.Logger.Warning("Couldn't find all corresponding trainCars from trainCarsToTransport!");
                            return;
                        }
                        // split the train into as few JobChains as possible
                        List<JobChainControllerWithEmptyHaulGeneration> jobChainControllers = null;
                        for (int lastCount = 0, div = 1; jobChainControllers == null && div <= trainCars.Count; div++)
                        {
                            int baseTrainCarsPerJob = trainCars.Count / div;
                            List<int> trainCarsPerJob = new List<int>(div);
                            for (int remainder = trainCars.Count % div - 1; remainder >= 0; remainder--)
                            {
                                trainCarsPerJob[remainder] += 1;
                            }
                            if (trainCarsPerJob[0] == lastCount)
                            {
                                // we've already tried a similar grouping and failed
                                continue;
                            }
                            lastCount = trainCarsPerJob[0];
                            List<JobChainControllerWithEmptyHaulGeneration> acc = new List<JobChainControllerWithEmptyHaulGeneration>();
                            for (int jobIdx = 0; jobIdx < trainCarsPerJob.Count; jobIdx++)
                            {
                                List<TrainCar> subsetTrainCars = trainCars.GetRange(trainCarsPerJob.GetRange(0, jobIdx).Sum(), trainCarsPerJob[jobIdx]);
                                JobChainControllerWithEmptyHaulGeneration jobChainController = TransportJobChainProceduralGenerator.GenerateTransportJobChainWithExistingCars(
                                    startingStation,
                                    track,
                                    subsetTrainCars,
                                    rng);
                                if (jobChainController != null)
                                {
                                    acc.Add(jobChainController);
                                }
                                else
                                {
                                    break;
                                }
                            }
                            if (acc.Count == trainCarsPerJob.Count)
                            {
                                jobChainControllers = acc;
                            }
                        }
                        if (jobChainControllers == null)
                        {
                            thisModEntry.Logger.Warning("Couldn't generate one or more JobChainController for trainCars from EmptyHaulJob!");
                            return;
                        }
                        foreach (JobChainControllerWithEmptyHaulGeneration jobChainController in jobChainControllers)
                        {
                            foreach (TrainCar trainCar in jobChainController.trainCarsForJobChain)
                            {
                                __instance.trainCarsForJobChain.Remove(trainCar);
                            }
                            jobChainController.FinalizeSetupAndGenerateFirstJob();
                            Debug.Log(string.Format("Generated job chain [{0}]: {1}", jobChainController.jobChainGO.name, jobChainController.jobChainGO));
                        }
                    }
                }
                catch (Exception e)
                {
                    thisModEntry.Logger.Error(string.Format("Exception thrown during JobChainController.OnLastJobInChainCompleted prefix patch:\n{0}", e.Message));
                    OnCriticalFailure();
                }
            }
        }
    }
}