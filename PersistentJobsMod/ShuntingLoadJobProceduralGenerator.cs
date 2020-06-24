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
            WarehouseMachine loadMachine = Utilities.GetRandomFromList(supportedWMCs, rng).warehouseMachine;
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
                    // trainCars not near any station
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
    }
}
