using System;
using System.Collections.Generic;
using DV.Logic.Job;
using UnityEngine;
using System.Reflection;

namespace PersistentJobsMod
{
    class TransportJobChainProceduralGenerator
    {
        public static JobChainControllerWithEmptyHaulGeneration GenerateTransportJobChainWithExistingCars(
            StationController startingStation,
            Track startingTrack,
            List<TrainCar> trainCars,
            System.Random rng)
        {
            // TODO: implement this!
            // get cargoTypes per carType
            // get inbound cargoTypesPerStation & intersect each with outbound cargoTypes of startingStation
            // choose a random cargoTypesPerStation (or randomly order the list of cargoTypesPerStation)
            // try to generate one or more jobs using the selected cargoTypesPerStation (or the first one from the randomly ordered list)


            // create the job chain controller
            GameObject gameObject = new GameObject(string.Format("ChainJob[{0}]: {1} - {2}", JobType.EmptyHaul, startingStation.logicStation.ID, destStation.logicStation.ID));
            gameObject.transform.SetParent(startingStation.transform);
            JobChainControllerWithEmptyHaulGeneration jobChainController = new JobChainControllerWithEmptyHaulGeneration(gameObject);
            jobChainController.trainCarsForJobChain = trainCars;
            // TODO: add staticShuntingLoadJobDefinition to job chain controller
            jobChainController.AddJobDefinitionToChain(GenerateTransportJobDefinition(gameObject, startingStation, destStation, startingTrack, destInboundTrack, ))
            // TODO: add staticShuntingUnloadJobDefinition to job chain contorller
        }

        private static StaticTransportJobDefinition GenerateTransportJobDefinition(
            GameObject gameObject,
            StationController startingStation,
            StationController destStation,
            Track startingTrack,
            Track destInboundTrack,
            List<TrainCar> trainCars,
            float trainLength,
            float bonusTimeLimit,
            float initialWage,
            JobLicenses requiredLicenses)
        {
            StationsChainData chainData = new StationsChainData(startingStation.stationInfo.YardID, destStation.stationInfo.YardID);
            List<Car> trainCarsToTransport = TrainCar.ExtractLogicCars(trainCars);
            StaticTransportJobDefinition staticTransportJobDefinition = gameObject.AddComponent<StaticTransportJobDefinition>();
            staticTransportJobDefinition.PopulateBaseJobDefinition(startingStation.logicStation, bonusTimeLimit, initialWage, chainData, requiredLicenses);
            staticTransportJobDefinition.startingTrack = startingTrack;
            staticTransportJobDefinition.trainCarsToTransport = trainCarsToTransport;
            staticTransportJobDefinition.destinationTrack = destInboundTrack;
            return staticTransportJobDefinition;
        }
    }
}
