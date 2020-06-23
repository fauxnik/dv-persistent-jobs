using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using DV.Logic.Job;

namespace PersistentJobsMod
{
    class TransportJobProceduralGenerator
    {
        public static JobChainControllerWithShuntingUnloadGeneration GenerateTransportJobWithExistingCars(
            StationController startingStation,
            Track startingTrack,
            StationController destStation,
            List<TrainCar> trainCars,
            List<CargoType> transportedCargoPerCar,
            List<float> cargoAmountPerCar,
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
            Track destinationTrack = yto.GetTrackThatHasEnoughFreeSpace(
                yto.FilterOutOccupiedTracks(destStation.logicStation.yard.TransferInTracks),
                trainLength
            );
            if (destinationTrack == null)
            {
                destinationTrack
                    = yto.GetTrackThatHasEnoughFreeSpace(destStation.logicStation.yard.TransferInTracks, trainLength);
            }
            if (destinationTrack == null)
            {
                Debug.LogWarning(string.Format(
                    "Could not create ChainJob[{0}]: {1} - {2}. Found no TransferInTrack with enough free space!",
                    JobType.Transport,
                    startingStation.logicStation.ID,
                    destStation.logicStation.ID
                ));
                return null;
            }
            List<TrainCarType> transportedCarTypes = (from tc in trainCars select tc.carType)
                .ToList<TrainCarType>();
            float bonusTimeLimit;
            float initialWage;
            Utilities.CalculateTransportBonusTimeLimitAndWage(
                startingStation,
                destStation,
                transportedCarTypes,
                transportedCargoPerCar,
                out bonusTimeLimit,
                out initialWage
            );
            JobLicenses requiredLicenses = LicenseManager.GetRequiredLicensesForJobType(JobType.Transport)
                | LicenseManager.GetRequiredLicensesForCarContainerTypes(hashSet)
                | LicenseManager.GetRequiredLicenseForNumberOfTransportedCars(trainCars.Count);
            return TransportJobProceduralGenerator.GenerateTransportChainController(
                startingStation,
                startingTrack,
                destStation,
                destinationTrack,
                trainCars,
                transportedCargoPerCar,
                cargoAmountPerCar,
                bonusTimeLimit,
                initialWage,
                requiredLicenses
            );
        }

        private static JobChainControllerWithShuntingUnloadGeneration GenerateTransportChainController(
            StationController startingStation,
            Track startingTrack,
            StationController destStation,
            Track destTrack,
            List<TrainCar> orderedTrainCars,
            List<CargoType> orderedCargoTypes,
            List<float> orderedCargoAmounts,
            float bonusTimeLimit,
            float initialWage,
            JobLicenses requiredLicenses)
        {
            GameObject gameObject = new GameObject(string.Format(
                "ChainJob[{0}]: {1} - {2}",
                JobType.Transport,
                startingStation.logicStation.ID,
                destStation.logicStation.ID
            ));
            gameObject.transform.SetParent(startingStation.transform);
            JobChainControllerWithShuntingUnloadGeneration jobChainController
                = new JobChainControllerWithShuntingUnloadGeneration(gameObject);
            StationsChainData chainData = new StationsChainData(
                startingStation.stationInfo.YardID,
                destStation.stationInfo.YardID
            );
            jobChainController.trainCarsForJobChain = orderedTrainCars;
            List<Car> orderedLogicCars = TrainCar.ExtractLogicCars(orderedTrainCars);
            StaticTransportJobDefinition staticTransportJobDefinition
                = gameObject.AddComponent<StaticTransportJobDefinition>();
            staticTransportJobDefinition.PopulateBaseJobDefinition(
                startingStation.logicStation,
                bonusTimeLimit,
                initialWage,
                chainData,
                requiredLicenses
            );
            staticTransportJobDefinition.startingTrack = startingTrack;
            staticTransportJobDefinition.destinationTrack = destTrack;
            staticTransportJobDefinition.trainCarsToTransport = orderedLogicCars;
            staticTransportJobDefinition.transportedCargoPerCar = orderedCargoTypes;
            staticTransportJobDefinition.cargoAmountPerCar = orderedCargoAmounts;
            jobChainController.AddJobDefinitionToChain(staticTransportJobDefinition);
            return jobChainController;
        }
    }
}
