using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Harmony12;
using UnityEngine;
using UnityModManagerNet;
using DV;
using DV.Logic.Job;

namespace PersistentJobsMod
{
    static class Main
    {
        private static UnityModManager.ModEntry thisModEntry;
        private static bool isModBroken = false;
        private static float initialDistanceRegular = 0f;
        private static float initialDistanceAnyJobTaken = 0f;
        public static float DVJobDestroyDistanceRegular { get { return initialDistanceRegular; } }

        static void Load(UnityModManager.ModEntry modEntry)
        {
            modEntry.Logger.Log("PersistenJobsMod.Load");
            var harmony = HarmonyInstance.Create(modEntry.Info.Id);
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            modEntry.OnToggle = OnToggle;
            thisModEntry = modEntry;
        }

        static bool OnToggle(UnityModManager.ModEntry modEntry, bool isTogglingOn)
        {
            modEntry.Logger.Log("PersistenJobsMod.OnToggle: " + isTogglingOn.ToString());

            float? carsCheckPeriod = Traverse.Create(SingletonBehaviour<UnusedTrainCarDeleter>.Instance)
                .Field("DELETE_CARS_CHECK_PERIOD")
                .GetValue<float>();
            if (carsCheckPeriod == null)
            {
                carsCheckPeriod = 0.5f;
            }
            SingletonBehaviour<UnusedTrainCarDeleter>.Instance.StopAllCoroutines();
            if (isTogglingOn && !isModBroken)
            {
                SingletonBehaviour<UnusedTrainCarDeleter>.Instance
                    .StartCoroutine(TrainCarsCreateJobOrDeleteCheck(Mathf.Max(carsCheckPeriod.Value, 1.0f)));
            }
            else
            {
                SingletonBehaviour<UnusedTrainCarDeleter>.Instance.StartCoroutine(
                    SingletonBehaviour<UnusedTrainCarDeleter>.Instance.TrainCarsDeleteCheck(carsCheckPeriod.Value)
                );
            }

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
            // prevents jobs from expiring due to the player's distance from the station
            static bool Prefix()
            {
                // skips the original method entirely when this mod is active
                return !thisModEntry.Active;
            }
        }

        [HarmonyPatch(typeof(StationJobGenerationRange))]
        [HarmonyPatchAll]
        class StationJobGenerationRange_AllMethods_Patch
        {
            // expands the distance at which the job generation trigger is rearmed
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
                        __instance.destroyGeneratedJobsSqrDistanceRegular =
                            __instance.destroyGeneratedJobsSqrDistanceAnyJobTaken;
                    }
                    else
                    {
                        __instance.destroyGeneratedJobsSqrDistanceRegular = initialDistanceRegular;
                        __instance.destroyGeneratedJobsSqrDistanceAnyJobTaken = initialDistanceAnyJobTaken;
                    }
                }
                catch (Exception e)
                {
                    thisModEntry.Logger.Error(string.Format(
                        "Exception thrown during StationJobGenerationRange.{0} prefix patch:\n{1}",
                        __originalMethod.Name,
                        e.Message
                    ));
                    OnCriticalFailure();
                }
            }
        }

        [HarmonyPatch(typeof(JobValidator), "ProcessJobOverview")]
        class JobValidator_ProcessJobOverview_Patch
        {
            // expires a job if none of its cars are in range of the starting station on job start attempt
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
                    Task taskWithCarsInRangeOfStation = job.tasks.FirstOrDefault((Task t) =>
                    {
                        List<Car> cars = Traverse.Create(t).Field("cars").GetValue<List<Car>>();
                        Car carInRangeOfStation = cars.FirstOrDefault((Car c) =>
                        {
                            TrainCar trainCar = TrainCar.GetTrainCarByCarGuid(c.carGuid);
                            float distance =
                                (trainCar.transform.position - stationRange.stationCenterAnchor.position).sqrMagnitude;
                            return trainCar != null && distance <= initialDistanceRegular;
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
                    thisModEntry.Logger.Error(string.Format(
                        "Exception thrown during JobValidator.ProcessJobOverview prefix patch:\n{0}",
                        e.Message
                    ));
                    OnCriticalFailure();
                }
            }
        }

        [HarmonyPatch(typeof(StationProceduralJobGenerator), "GenerateJobChain")]
        class StationProceduralJobGenerator_GenerateJobChain_Patch
        {
            // copied from the patched method; may help keep mod stable across game updates
            static bool Prefix(
                System.Random rng,
                bool forceJobWithLicenseRequirementFulfilled,
                ref StationProceduralJobGenerator __instance,
                ref JobChainController __result,
                StationProceduralJobsRuleset ___generationRuleset,
                ref System.Random ___currentRng,
                YardTracksOrganizer ___yto,
                Yard ___stYard)
            {
                if (thisModEntry.Active)
                {
                    try
                    {
                        if (
                            !___generationRuleset.loadStartingJobSupported &&
                            !___generationRuleset.haulStartingJobSupported &&
                            !___generationRuleset.unloadStartingJobSupported &&
                            !___generationRuleset.emptyHaulStartingJobSupported)
                        {
                            return true;
                        }
                        ___currentRng = rng;
                        List<JobType> spawnableJobTypes = new List<JobType>();
                        if (___generationRuleset.loadStartingJobSupported)
                        {
                            spawnableJobTypes.Add(JobType.ShuntingLoad);
                        }
                        if (___generationRuleset.emptyHaulStartingJobSupported)
                        {
                            spawnableJobTypes.Add(JobType.EmptyHaul);
                        }
                        int countOutTracksAvailable = ___yto.FilterOutOccupiedTracks(___stYard.TransferOutTracks).Count;
                        if (___generationRuleset.haulStartingJobSupported && countOutTracksAvailable > 0)
                        {
                            spawnableJobTypes.Add(JobType.Transport);
                        }
                        int countInTracksAvailable = ___yto.FilterOutReservedTracks(
                            ___yto.FilterOutOccupiedTracks(___stYard.TransferInTracks)
                        ).Count;
                        if (___generationRuleset.unloadStartingJobSupported && countInTracksAvailable > 0)
                        {
                            spawnableJobTypes.Add(JobType.ShuntingUnload);
                        }
                        JobChainController jobChainController = null;
                        if (forceJobWithLicenseRequirementFulfilled)
                        {
                            if (
                                spawnableJobTypes.Contains(JobType.Transport) &&
                                LicenseManager.IsJobLicenseAcquired(JobLicenses.FreightHaul))
                            {
                                jobChainController = Traverse.Create(__instance)
                                    .Method("GenerateOutChainJob")
                                    .GetValue<JobChainController>(JobType.Transport, true);
                                if (jobChainController != null)
                                {
                                    __result = jobChainController;
                                    return false;
                                }
                            }
                            if (
                                spawnableJobTypes.Contains(JobType.EmptyHaul) &&
                                LicenseManager.IsJobLicenseAcquired(JobLicenses.LogisticalHaul))
                            {
                                jobChainController = Traverse.Create(__instance)
                                    .Method("GenerateEmptyHaul")
                                    .GetValue<JobChainController>(true);
                                if (jobChainController != null)
                                {
                                    __result = jobChainController;
                                    return false;
                                }
                            }
                            if (
                                spawnableJobTypes.Contains(JobType.ShuntingLoad) &&
                                LicenseManager.IsJobLicenseAcquired(JobLicenses.Shunting))
                            {
                                jobChainController = Traverse.Create(__instance)
                                    .Method("GenerateOutChainJob")
                                    .GetValue<JobChainController>(JobType.ShuntingLoad, true);
                                if (jobChainController != null)
                                {
                                    __result = jobChainController;
                                    return false;
                                }
                            }
                            if (
                                spawnableJobTypes.Contains(JobType.ShuntingUnload) &&
                                LicenseManager.IsJobLicenseAcquired(JobLicenses.Shunting))
                            {
                                jobChainController = Traverse.Create(__instance)
                                    .Method("GenerateInChainJob")
                                    .GetValue<JobChainController>(JobType.ShuntingUnload, true);
                                if (jobChainController != null)
                                {
                                    __result = jobChainController;
                                    return false;
                                }
                            }
                            __result = null;
                            return false;
                        }
                        if (
                            spawnableJobTypes.Contains(JobType.Transport) &&
                            countOutTracksAvailable > Mathf.FloorToInt(0.399999976f * (float)___stYard.TransferOutTracks.Count))
                        {
                            JobType startingJobType = JobType.Transport;
                            jobChainController = Traverse.Create(__instance)
                                .Method("GenerateOutChainJob")
                                .GetValue<JobChainController>(startingJobType, false);
                        }
                        else
                        {
                            if (spawnableJobTypes.Count == 0)
                            {
                                __result = null;
                                return false;
                            }
                            JobType startingJobType = Traverse.Create(__instance)
                                .Method("GetRandomFromList")
                                .GetValue<JobType>(spawnableJobTypes);
                            switch (startingJobType)
                            {
                                case JobType.ShuntingLoad:
                                case JobType.Transport:
                                    jobChainController = Traverse.Create(__instance)
                                        .Method("GenerateOutChainJob")
                                        .GetValue<JobChainController>(startingJobType, false);
                                    break;
                                case JobType.ShuntingUnload:
                                    jobChainController = Traverse.Create(__instance)
                                        .Method("GenerateInChainJob")
                                        .GetValue<JobChainController>(startingJobType, false);
                                    break;
                                case JobType.EmptyHaul:
                                    jobChainController = Traverse.Create(__instance)
                                        .Method("GenerateEmptyHaul")
                                        .GetValue<JobChainController>(false);
                                    break;
                            }
                        }
                        ___currentRng = null;
                        __result = jobChainController;
                        return false;
                    }
                    catch (Exception e)
                    {
                        thisModEntry.Logger.Error(string.Format(
                            "Exception thrown during {0}.{1} {2} patch:\n{3}",
                            "StationProceduralJobGenerator",
                            "GenerateJobChain",
                            "prefix",
                            e.Message
                        ));
                        OnCriticalFailure();
                    }
                }
                return true;
            }
        }

        [HarmonyPatch(typeof(StationProceduralJobGenerator), "GenerateInChainJob")]
        class StationProceduralJobGenerator_GenerateInChainJob_Patch
        {
            // generates shunting unload jobs
            static bool Prefix()
            {
                if (thisModEntry.Active)
                {
                    try
                    {
                        // TODO: implement this!
                    }
                    catch (Exception e)
                    {
                        thisModEntry.Logger.Error(string.Format(
                            "Exception thrown during {0}.{1} {2} patch:\n{3}",
                            "StationProceduralJobGenerator",
                            "GenerateInChainJob",
                            "prefix",
                            e.Message
                        ));
                        OnCriticalFailure();
                    }
                }
                return true;
            }
        }

        [HarmonyPatch(typeof(StationProceduralJobGenerator), "GenerateOutChainJob")]
        class StationProceduralJobGenerator_GenerateOutChainJob_Patch
        {
            // generates shunting load jobs & freight haul jobs
            static bool Prefix()
            {
                if (thisModEntry.Active)
                {
                    try
                    {
                        // TODO: implement this!
                    }
                    catch (Exception e)
                    {
                        thisModEntry.Logger.Error(string.Format(
                            "Exception thrown during {0}.{1} {2} patch:\n{3}",
                            "StationProceduralJobGenerator",
                            "GenerateOutChainJob",
                            "prefix",
                            e.Message
                        ));
                        OnCriticalFailure();
                    }
                }
                return true;
            }
        }

        [HarmonyPatch(typeof(StationProceduralJobGenerator), "GenerateEmptyHaul")]
        class StationProceduralJobGenerator_GenerateEmptyHaul_Patch
        {
            // generates logistical haul jobs
            static bool Prefix()
            {
                if (thisModEntry.Active)
                {
                    try
                    {
                        // TODO: implement this!
                    }
                    catch (Exception e)
                    {
                        thisModEntry.Logger.Error(string.Format(
                            "Exception thrown during {0}.{1} {2} patch:\n{3}",
                            "StationProceduralJobGenerator",
                            "GenerateEmptyHaul",
                            "prefix",
                            e.Message
                        ));
                        OnCriticalFailure();
                    }
                }
                return true;
            }
        }

        [HarmonyPatch(typeof(UnusedTrainCarDeleter), "InstantConditionalDeleteOfUnusedCars")]
        class UnusedTrainCarDeleter_InstantConditionalDeleteOfUnusedCars_Patch
        {
            // tries to generate new shunting load jobs for the train cars marked for deletion
            // failing that, the train cars are deleted
            public bool Prefix(
                UnusedTrainCarDeleter __instance,
                List<TrainCar> ___unusedTrainCarsMarkedForDelete,
                Dictionary<TrainCar, CarVisitChecker> ___carVisitCheckersMap)
            {
                if (thisModEntry.Active)
                {
                    try
                    {
                        if (___unusedTrainCarsMarkedForDelete.Count == 0)
                        {
                            return false;
                        }

                        List<TrainCar> trainCarsToDelete = new List<TrainCar>();
                        for (int i = ___unusedTrainCarsMarkedForDelete.Count - 1; i >= 0; i--)
                        {
                            TrainCar trainCar = ___unusedTrainCarsMarkedForDelete[i];
                            if (trainCar == null)
                            {
                                ___unusedTrainCarsMarkedForDelete.RemoveAt(i);
                                continue;
                            }
                            bool areDeleteConditionsFulfilled = Traverse.Create(__instance)
                                .Method("AreDeleteConditionsFulfilled")
                                .GetValue<bool>(trainCar);
                            if (areDeleteConditionsFulfilled)
                            {
                                ___unusedTrainCarsMarkedForDelete.RemoveAt(i);
                                trainCarsToDelete.Add(trainCar);
                                ___carVisitCheckersMap.Remove(trainCar);
                            }
                        }
                        if (trainCarsToDelete.Count == 0)
                        {
                            return false;
                        }

                        // TODO: add job creation logic

                        SingletonBehaviour<CarSpawner>.Instance.DeleteTrainCars(trainCarsToDelete, true);
                        return false;
                    }
                    catch (Exception e)
                    {
                        thisModEntry.Logger.Error(string.Format(
                            "Exception thrown during {0}.{1} {2} patch:\n{3}",
                            "UnusedTrainCarDeleter",
                            "InstantConditionalDeleteOfUnusedCars",
                            "prefix",
                            e.Message
                        ));
                        OnCriticalFailure();
                    }
                }
                return true;
            }
        }

        // override/replacement for UnusedTrainCarDeleter.TrainCarsDeleteCheck coroutine
        // tries to generate new shunting load jobs for the train cars marked for deletion
        // failing that, the train cars are deleted
        public static IEnumerator TrainCarsCreateJobOrDeleteCheck(float period)
        {
            List<TrainCar> trainCarsToDelete = null;
            List<TrainCar> trainCarCandidatesForDelete = null;
            Traverse unusedTrainCarDeleterTraverser = null;
            List<TrainCar> unusedTrainCarsMarkedForDelete = null;
            Dictionary<TrainCar, DV.CarVisitChecker> carVisitCheckersMap = null;
            Traverse AreDeleteConditionsFulfilledMethod = null;
            try
            {
                trainCarsToDelete = new List<TrainCar>();
                trainCarCandidatesForDelete = new List<TrainCar>();
                unusedTrainCarDeleterTraverser = Traverse.Create(SingletonBehaviour<UnusedTrainCarDeleter>.Instance);
                unusedTrainCarsMarkedForDelete = unusedTrainCarDeleterTraverser
                    .Field("unusedTrainCarsMarkedForDelete")
                    .GetValue<List<TrainCar>>();
                carVisitCheckersMap = unusedTrainCarDeleterTraverser
                    .Field("carVisitCheckersMap")
                    .GetValue<Dictionary<TrainCar, DV.CarVisitChecker>>();
                AreDeleteConditionsFulfilledMethod = unusedTrainCarDeleterTraverser.Method("AreDeleteConditionsFulfilled");
            }
            catch (Exception e)
            {
                thisModEntry.Logger.Error(string.Format(
                    "Exception thrown during TrainCarsCreateJobOrDeleteCheck setup:\n{0}",
                    e.Message
                ));
                OnCriticalFailure();
            }
            for (; ; )
            {
                yield return WaitFor.SecondsRealtime(period);

                try
                {
                    if (PlayerManager.PlayerTransform == null || FastTravelController.IsFastTravelling)
                    {
                        continue;
                    }

                    if (unusedTrainCarsMarkedForDelete.Count == 0)
                    {
                        if (carVisitCheckersMap.Count != 0)
                        {
                            carVisitCheckersMap.Clear();
                        }
                        continue;
                    }
                }
                catch (Exception e)
                {
                    thisModEntry.Logger.Error(string.Format(
                        "Exception thrown during TrainCarsCreateJobOrDeleteCheck skip checks:\n{0}",
                        e.Message
                    ));
                    OnCriticalFailure();
                }

                try
                {
                    trainCarCandidatesForDelete.Clear();
                    for (int i = unusedTrainCarsMarkedForDelete.Count - 1; i >= 0; i--)
                    {
                        TrainCar trainCar = unusedTrainCarsMarkedForDelete[i];
                        if (trainCar == null)
                        {
                            unusedTrainCarsMarkedForDelete.RemoveAt(i);
                        }
                        else if (AreDeleteConditionsFulfilledMethod.GetValue<bool>(trainCar))
                        {
                            unusedTrainCarsMarkedForDelete.RemoveAt(i);
                            trainCarCandidatesForDelete.Add(trainCar);
                        }
                    }
                    if (trainCarCandidatesForDelete.Count == 0)
                    {
                        continue;
                    }
                }
                catch (Exception e)
                {
                    thisModEntry.Logger.Error(string.Format(
                        "Exception thrown during TrainCarsCreateJobOrDeleteCheck delete candidate collection:\n{0}",
                        e.Message
                    ));
                    OnCriticalFailure();
                }

                yield return WaitFor.SecondsRealtime(period);

                try
                {
                    // TODO: add job creation logic
                    List<StationController> allStationControllers
                        = SingletonBehaviour<LogicController>.Instance.GetComponents<StationController>().ToList();

                    // group trainCars by trainset
                    Dictionary<Trainset, List<TrainCar>> trainCarsPerTrainSet
                        = ShuntingLoadJobProceduralGenerator.GroupTrainCarsByTrainset(trainCarCandidatesForDelete);

                    // group trainCars sets by nearest stationController
                    Dictionary<StationController, List<(List<TrainCar>, List<CargoGroup>)>> cgsPerTcsPerSc
                        = ShuntingLoadJobProceduralGenerator.GroupTrainCarSetsByNearestStation(trainCarsPerTrainSet);

                    // populate possible cargoGroups per group of trainCars
                    ShuntingLoadJobProceduralGenerator.PopulateCargoGroupsPerTrainCarSet(cgsPerTcsPerSc);

                    // pick new jobs for the trainCars at each station
                    System.Random rng = new System.Random(Environment.TickCount);
                    int maxCarsLicensed = LicenseManager.GetMaxNumberOfCarsPerJobWithAcquiredJobLicenses();
                    List<(StationController, List<CarsPerTrack>, StationController, List<TrainCar>, List<CargoType>)> jobsToGenerate
                        = new List<(StationController, List<CarsPerTrack>, StationController, List<TrainCar>, List<CargoType>)>();
                    foreach (StationController startingStation in cgsPerTcsPerSc.Keys)
                    {
                        bool hasFulfilledLicenseReqs = false;
                        List<(List<TrainCar>, List<CargoGroup>)> cgsPerTcs = cgsPerTcsPerSc[startingStation];

                        while (cgsPerTcs.Count > 0)
                        {
                            List<TrainCar> trainCarsToLoad = new List<TrainCar>();
                            IEnumerable<CargoGroup> cargoGroupsToUse = new HashSet<CargoGroup>();
                            int countTracks = rng.Next(1, startingStation.proceduralJobsRuleset.maxShuntingStorageTracks + 1);
                            int triesLeft = cgsPerTcs.Count;
                            bool isFulfillingLicenseReqs = false;

                            for (; countTracks > 0 && triesLeft > 0; triesLeft--)
                            {
                                (List<TrainCar> trainCarsToAdd, List<CargoGroup> availableCargoGroups)
                                    = cgsPerTcs[cgsPerTcs.Count - 1];

                                List<CargoGroup> licensedCargoGroups
                                    = (from cg in availableCargoGroups
                                       where LicenseManager.IsLicensedForJob(cg.CargoRequiredLicenses)
                                       select cg).ToList();

                                // determine which cargoGroups to choose from
                                if (trainCarsToLoad.Count == 0)
                                {
                                    if (!hasFulfilledLicenseReqs &&
                                        licensedCargoGroups.Count > 0 &&
                                        trainCarsToAdd.Count <= maxCarsLicensed)
                                    {
                                        isFulfillingLicenseReqs = true;
                                    }
                                }
                                else if (isFulfillingLicenseReqs &&
                                        (licensedCargoGroups.Count == 0 ||
                                        cargoGroupsToUse.Intersect(licensedCargoGroups).Count() == 0 ||
                                        trainCarsToLoad.Count + trainCarsToAdd.Count <= maxCarsLicensed) ||
                                        cargoGroupsToUse.Intersect(availableCargoGroups).Count() == 0)
                                {
                                    // either trying to satisfy licenses, but these trainCars aren't compatible
                                    //   or the cargoGroups for these trainCars aren't compatible
                                    // shuffle them to the front and try again
                                    cgsPerTcs.Insert(0, cgsPerTcs[cgsPerTcs.Count - 1]);
                                    cgsPerTcs.RemoveAt(cgsPerTcs.Count - 1);
                                    continue;
                                }
                                availableCargoGroups
                                    = isFulfillingLicenseReqs ? licensedCargoGroups : availableCargoGroups;

                                // if we've made it this far, we can add these trainCars to the job
                                cargoGroupsToUse = cargoGroupsToUse.Intersect(availableCargoGroups);
                                trainCarsToLoad.AddRange(trainCarsToAdd);
                                cgsPerTcs.RemoveAt(cgsPerTcs.Count - 1);
                                countTracks--;
                            }

                            if (trainCarsToLoad.Count == 0)
                            {
                                // no more jobs can be made from these trainCar sets; abandon the rest
                                break;
                            }

                            // if we're fulfilling license requirements this time around,
                            // we won't need to try again for this station
                            hasFulfilledLicenseReqs = isFulfillingLicenseReqs;

                            CargoGroup chosenCargoGroup
                                = Utilities.GetRandomFromEnumerable<CargoGroup>(cargoGroupsToUse, rng);
                            StationController destStation
                                = Utilities.GetRandomFromEnumerable<StationController>(chosenCargoGroup.stations, rng);
                            Dictionary<Track, List<Car>> carsPerTrackDict = new Dictionary<Track, List<Car>>();
                            foreach (TrainCar trainCar in trainCarsToLoad)
                            {
                                Track track = trainCar.logicCar.CurrentTrack;
                                if (!carsPerTrackDict.ContainsKey(track))
                                {
                                    carsPerTrackDict[track] = new List<Car>();
                                }
                                carsPerTrackDict[track].Add(trainCar.logicCar);
                            }

                            // populate all the info; we'll generate the jobs later
                            jobsToGenerate.Add((
                                startingStation,
                                carsPerTrackDict.Keys.Select(
                                    track => new CarsPerTrack(track, carsPerTrackDict[track])).ToList(),
                                destStation,
                                trainCarsToLoad,
                                trainCarsToLoad.Select(
                                    tc => Utilities.GetRandomFromEnumerable<CargoType>(
                                        chosenCargoGroup.cargoTypes.Intersect(
                                            Utilities.GetCargoTypesForCarType(tc.carType)),
                                        rng)).ToList()));
                        }
                    }

                    // try to generate jobs
                    IEnumerable<(List<TrainCar> , JobChainController)> trainCarListJobChainControllerPairs
                        = jobsToGenerate.Select((definition) =>
                    {
                        // I miss having a spread operator :(
                        (StationController ss, List<CarsPerTrack> cpst, StationController ds, _, _) = definition;
                        (_, _, _, List<TrainCar> tcs, List<CargoType> cts) = definition;

                        return (tcs, (JobChainController)ShuntingLoadJobProceduralGenerator
                            .GenerateShuntingLoadJobWithExistingCars(ss, cpst, ds, tcs, cts, rng));
                    });

                    // prevent deletion of trainCars for which a new job was generated
                    foreach ((List<TrainCar> trainCars, JobChainController jcc) in trainCarListJobChainControllerPairs)
                    {
                        if (jcc != null)
                        {
                            trainCars.ForEach(tc => trainCarCandidatesForDelete.Remove(tc));
                        }
                    }
                }
                catch (Exception e)
                {
                    thisModEntry.Logger.Error(string.Format(
                        "Exception thrown during TrainCarsCreateJobOrDeleteCheck job creation:\n{0}",
                        e.Message
                    ));
                    OnCriticalFailure();
                }

                yield return WaitFor.SecondsRealtime(period);

                try
                {
                    trainCarsToDelete.Clear();
                    for (int j = trainCarCandidatesForDelete.Count - 1; j >= 0; j--)
                    {
                        TrainCar trainCar2 = trainCarCandidatesForDelete[j];
                        if (trainCar2 == null)
                        {
                            trainCarCandidatesForDelete.RemoveAt(j);
                        }
                        else if (AreDeleteConditionsFulfilledMethod.GetValue<bool>(trainCar2))
                        {
                            trainCarCandidatesForDelete.RemoveAt(j);
                            carVisitCheckersMap.Remove(trainCar2);
                            trainCarsToDelete.Add(trainCar2);
                        }
                        else
                        {
                            Debug.LogWarning(string.Format(
                                "Returning {0} to unusedTrainCarsMarkedForDelete list. PlayerTransform was outside" +
                                " of DELETE_SQR_DISTANCE_FROM_TRAINCAR range of train car, but after short period it" +
                                " was back in range!",
                                trainCar2.name
                            ));
                            trainCarCandidatesForDelete.RemoveAt(j);
                            unusedTrainCarsMarkedForDelete.Add(trainCar2);
                        }
                    }
                    if (trainCarsToDelete.Count != 0)
                    {
                        SingletonBehaviour<CarSpawner>.Instance
                            .DeleteTrainCars(new List<TrainCar>(trainCarsToDelete), false);
                    }
                }
                catch (Exception e)
                {
                    thisModEntry.Logger.Error(string.Format(
                        "Exception thrown during TrainCarsCreateJobOrDeleteCheck car deletion:\n{0}",
                        e.Message
                    ));
                    OnCriticalFailure();
                }
            }
        }

        [HarmonyPatch(typeof(YardTracksOrganizer), "GetTrackThatHasEnoughFreeSpace")]
        class YardTracksOrganizer_GetTrackThatHasEnoughFreeSpace_Patch
        {
            // chooses the shortest track with enough space (instead of the first track found)
            static bool Prefix(List<Track> tracks, float requiredLength, YardTracksOrganizer __instance, ref Track __result)
            {
                if (thisModEntry.Active)
                {
                    try
                    {
                        __result = null;
                        SortedList<double, Track> tracksSortedByLength = new SortedList<double, Track>();
                        foreach (Track track in tracks)
                        {
                            double freeSpaceOnTrack = __instance.GetFreeSpaceOnTrack(track);
                            if (freeSpaceOnTrack > (double)requiredLength)
                            {
                                tracksSortedByLength.Add(freeSpaceOnTrack, track);
                            }
                        }
                        if (tracksSortedByLength.Count > 0)
                        {
                            __result = tracksSortedByLength[0];
                        }
                        return false;
                    }
                    catch (Exception e)
                    {
                        thisModEntry.Logger.Error(string.Format(
                            "Exception thrown during {0}.{1} {2} patch:\n{3}",
                            "YardTracksOrganizer",
                            "GetTrackThatHasEnoughFreeSpace",
                            "prefix",
                            e.Message
                        ));
                        OnCriticalFailure();
                    }
                }
                return true;
            }
        }
    }
}