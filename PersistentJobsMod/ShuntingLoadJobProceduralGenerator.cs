using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using DV.Logic.Job;

namespace PersistentJobsMod
{
    class ShuntingLoadJobProceduralGenerator
    {
        public static JobChainControllerWithTransportGeneration GenerateShuntingLoadJobWithExistingCars(
            StationController startingStation,
            List<CarsPerTrack> carsPerStartingTrack,
            StationController destStation,
            List<TrainCar> trainCars,
            List<CargoType> transportedCargoPerCar,
            System.Random rng)
        {
            YardTracksOrganizer yto = YardTracksOrganizer.Instance;
            HashSet<CargoContainerType> hashSet = new HashSet<CargoContainerType>();
            for (int i = 0; i < trainCars.Count; i++)
            {
                hashSet.Add(CargoTypes.CarTypeToContainerType[trainCars[i].carType]);
            }
            float trainLength = yto.GetTotalTrainCarsLength(trainCars)
                + yto.GetSeparationLengthBetweenCars(trainCars.Count);
            List<WarehouseMachineController> supportedWMCs = startingStation.warehouseMachineControllers
                    .Where(wm => wm.supportedCargoTypes.Intersect(transportedCargoPerCar).Count() > 0)
                    .ToList();
            if (supportedWMCs.Count == 0)
            {
                Debug.LogWarning(string.Format(
                    "Could not create ChainJob[{0}]: {1} - {2}. Found no supported WarehouseMachine!",
                    JobType.ShuntingLoad,
                    startingStation.logicStation.ID,
                    destStation.logicStation.ID
                ));
                return null;
            }
            WarehouseMachine loadMachine = Utilities.GetRandomFromEnumerable(supportedWMCs, rng).warehouseMachine;
            Track destinationTrack = yto.GetTrackThatHasEnoughFreeSpace(
                yto.FilterOutOccupiedTracks(startingStation.logicStation.yard.TransferOutTracks),
                trainLength
            );
            if (destinationTrack == null)
            {
                destinationTrack = yto.GetTrackThatHasEnoughFreeSpace(
                    startingStation.logicStation.yard.TransferOutTracks,
                    trainLength
                );
            }
            if (destinationTrack == null)
            {
                Debug.LogWarning(string.Format(
                    "Could not create ChainJob[{0}]: {1} - {2}. Found no TransferOutTrack with enough free space!",
                    JobType.ShuntingLoad,
                    startingStation.logicStation.ID,
                    destStation.logicStation.ID
                ));
                return null;
            }
            List<TrainCarType> transportedCarTypes = (from tc in trainCars select tc.carType)
                .ToList<TrainCarType>();
            float bonusTimeLimit;
            float initialWage;
            Utilities.CalculateShuntingBonusTimeLimitAndWage(
                JobType.ShuntingLoad,
                carsPerStartingTrack.Count,
                transportedCarTypes,
                transportedCargoPerCar,
                out bonusTimeLimit,
                out initialWage
            );
            JobLicenses requiredLicenses = LicenseManager.GetRequiredLicensesForJobType(JobType.Transport)
                | LicenseManager.GetRequiredLicensesForCarContainerTypes(hashSet)
                | LicenseManager.GetRequiredLicenseForNumberOfTransportedCars(trainCars.Count);
            return GenerateShuntingLoadChainController(
                startingStation,
                carsPerStartingTrack,
                loadMachine,
                destStation,
                destinationTrack,
                trainCars,
                transportedCargoPerCar,
                (from tc in trainCars select 1.0f).ToList(),
                bonusTimeLimit,
                initialWage,
                requiredLicenses
            );
        }

        private static JobChainControllerWithTransportGeneration GenerateShuntingLoadChainController(
            StationController startingStation,
            List<CarsPerTrack> carsPerStartingTrack,
            WarehouseMachine loadMachine,
            StationController destStation,
            Track destinationTrack,
            List<TrainCar> orderedTrainCars,
            List<CargoType> orderedCargoTypes,
            List<float> orderedCargoAmounts,
            float bonusTimeLimit,
            float initialWage,
            JobLicenses requiredLicenses)
        {
            GameObject gameObject = new GameObject(string.Format(
                "ChainJob[{0}]: {1} - {2}",
                JobType.ShuntingLoad,
                startingStation.logicStation.ID,
                destStation.logicStation.ID
            ));
            gameObject.transform.SetParent(startingStation.transform);
            JobChainControllerWithTransportGeneration jobChainController
                = new JobChainControllerWithTransportGeneration(gameObject);
            StationsChainData chainData = new StationsChainData(
                startingStation.stationInfo.YardID,
                destStation.stationInfo.YardID
            );
            jobChainController.trainCarsForJobChain = orderedTrainCars;
            Dictionary<CargoType, List<(TrainCar, float)>> cargoTypeToTrainCarAndAmount
                = new Dictionary<CargoType, List<(TrainCar, float)>>();
            for (int i = 0; i < orderedTrainCars.Count; i++)
            {
                if (!cargoTypeToTrainCarAndAmount.ContainsKey(orderedCargoTypes[i]))
                {
                    cargoTypeToTrainCarAndAmount[orderedCargoTypes[i]] = new List<(TrainCar, float)>();
                }
                cargoTypeToTrainCarAndAmount[orderedCargoTypes[i]].Add((orderedTrainCars[i], orderedCargoAmounts[i]));
            }
            List<CarsPerCargoType> loadData = cargoTypeToTrainCarAndAmount.Select(
                kvPair => new CarsPerCargoType(
                    kvPair.Key,
                    kvPair.Value.Select(t => t.Item1.logicCar).ToList(),
                    kvPair.Value.Aggregate(0.0f, (sum, t) => sum + t.Item2)
                )).ToList();
            StaticShuntingLoadJobDefinition staticShuntingLoadJobDefinition
                = gameObject.AddComponent<StaticShuntingLoadJobDefinition>();
            staticShuntingLoadJobDefinition.PopulateBaseJobDefinition(
                startingStation.logicStation,
                bonusTimeLimit,
                initialWage,
                chainData,
                requiredLicenses
            );
            staticShuntingLoadJobDefinition.carsPerStartingTrack = carsPerStartingTrack;
            staticShuntingLoadJobDefinition.destinationTrack = destinationTrack;
            staticShuntingLoadJobDefinition.loadData = loadData;
            staticShuntingLoadJobDefinition.loadMachine = loadMachine;
            jobChainController.AddJobDefinitionToChain(staticShuntingLoadJobDefinition);
            return jobChainController;
        }

        public static Dictionary<Trainset, List<TrainCar>> GroupTrainCarsByTrainset(List<TrainCar> trainCars)
        {
            Dictionary<Trainset, List<TrainCar>> trainCarsPerTrainSet = new Dictionary<Trainset, List<TrainCar>>();
            foreach (TrainCar tc in trainCars)
            {
                if (tc != null)
                {
                    if (trainCarsPerTrainSet[tc.trainset] == null)
                    {
                        trainCarsPerTrainSet[tc.trainset] = new List<TrainCar>();
                    }
                    trainCarsPerTrainSet[tc.trainset].Add(tc);
                }
            }
            return trainCarsPerTrainSet;
        }

        // cargoGroup lists will be unpopulated; use PopulateCargoGroupsPerTrainCarSet to fill in this data
        public static Dictionary<StationController, List<(List<TrainCar>, List<CargoGroup>)>>
            GroupTrainCarSetsByNearestStation(Dictionary<Trainset, List<TrainCar>> trainCarsPerTrainSet)
        {
            StationController[] stationControllers
                = SingletonBehaviour<LogicController>.Instance.GetComponents<StationController>();
            Dictionary<StationController, List<(List<TrainCar>, List<CargoGroup>)>> cgsPerTcsPerSc
                        = new Dictionary<StationController, List<(List<TrainCar>, List<CargoGroup>)>>();
            float abandonmentThreshold = 1.2f * Main.DVJobDestroyDistanceRegular;
            foreach (Trainset ts in trainCarsPerTrainSet.Keys)
            {
                List<TrainCar> tcs = trainCarsPerTrainSet[ts];
                SortedList<float, StationController> stationsByDistance
                    = new SortedList<float, StationController>();
                foreach (StationController sc in stationControllers)
                {
                    // since all trainCars in the trainset are coupled,
                    // use the position of the first one to approximate the position of the trainset
                    float distance = (tcs[0].transform.position - sc.transform.position).sqrMagnitude;
                    // only create jobs for trainCars within a reasonable range of a station
                    if (distance < abandonmentThreshold)
                    {
                        stationsByDistance.Add(distance, sc);
                    }
                }
                if (stationsByDistance.Count == 0)
                {
                    // trainCars not near any station; abandon them
                    continue;
                }
                // the first station is the closest
                if (cgsPerTcsPerSc[stationsByDistance[0]] == null)
                {
                    cgsPerTcsPerSc[stationsByDistance[0]] = new List<(List<TrainCar>, List<CargoGroup>)>();
                }
                cgsPerTcsPerSc[stationsByDistance[0]].Add((tcs, new List<CargoGroup>()));
            }
            return cgsPerTcsPerSc;
        }

        public static void PopulateCargoGroupsPerTrainCarSet(
            Dictionary<StationController, List<(List<TrainCar>, List<CargoGroup>)>> cgsPerTcsPerSc)
        {
            foreach (StationController sc in cgsPerTcsPerSc.Keys)
            {
                foreach ((List<TrainCar>, List<CargoGroup>) cgsPerTcs in cgsPerTcsPerSc[sc])
                {
                    if (cgsPerTcs.Item2.Count > 0)
                    {
                        Debug.LogWarning(
                            "Unexpected CargoGroup data in PopulateCargoGroupsPerTrainCarSet! Proceding to overwrite."
                        );
                    }
                    List<CargoGroup> availableCargoGroups = new List<CargoGroup>();
                    foreach (CargoGroup cg in sc.proceduralJobsRuleset.outputCargoGroups)
                    {
                        // ensure all trainCars will have at least one cargoType to haul
                        IEnumerable<IEnumerable<CargoType>> outboundCargoTypesPerTrainCar
                            = (from tc in cgsPerTcs.Item1
                               select Utilities.GetCargoTypesForCarType(tc.carType).Intersect(cg.cargoTypes));
                        if (outboundCargoTypesPerTrainCar.All(cgs => cgs.Count() > 0))
                        {
                            cgsPerTcs.Item2.Add(cg);
                        }
                    }
                }
            }
        }

        public static List<(StationController, List<CarsPerTrack>, StationController, List<TrainCar>, List<CargoType>)>
            ComputeJobInfosFromCargoGroupsPerTrainCarSetPerStation(
                Dictionary<StationController, List<(List<TrainCar>, List<CargoGroup>)>> cgsPerTcsPerSc,
                System.Random rng)
        {
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
                        // no more jobs can be made from the trainCar sets at this station; abandon the rest
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

            return jobsToGenerate;
        }

        public static IEnumerable<(List<TrainCar>, JobChainController)> doJobGeneration(
            List<(StationController, List<CarsPerTrack>, StationController, List<TrainCar>, List<CargoType>)> jobInfos,
            System.Random rng)
        {
            return jobInfos.Select((definition) =>
            {
                // I miss having a spread operator :(
                (StationController ss, List<CarsPerTrack> cpst, StationController ds, _, _) = definition;
                (_, _, _, List<TrainCar> tcs, List<CargoType> cts) = definition;

                return (tcs, (JobChainController)GenerateShuntingLoadJobWithExistingCars(ss, cpst, ds, tcs, cts, rng));
            });
        }
    }
}
