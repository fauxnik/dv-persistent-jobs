using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using DV.Logic.Job;

namespace PersistentJobsMod
{
	class ShuntingUnloadJobProceduralGenerator
	{
		public static JobChainController GenerateShuntingUnloadJobWithExistingCars(
			StationController startingStation,
			Track startingTrack,
			StationController destStation,
			List<TrainCar> trainCars,
			List<CargoType> transportedCargoPerCar,
			System.Random rng,
			bool forceCorrectCargoStateOnCars = false)
		{
			YardTracksOrganizer yto = YardTracksOrganizer.Instance;
			HashSet<CargoContainerType> carContainerTypes = new HashSet<CargoContainerType>();
			for (int i = 0; i < trainCars.Count; i++)
			{
				carContainerTypes.Add(CargoTypes.CarTypeToContainerType[trainCars[i].carType]);
			}
			float approxTrainLength = yto.GetTotalTrainCarsLength(trainCars)
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
					destStation.logicStation.ID));
				return null;
			}
			WarehouseMachine loadMachine = Utilities.GetRandomFromEnumerable(supportedWMCs, rng).warehouseMachine;

			// choose destination tracks
			int countTracks = rng.Next(1, startingStation.proceduralJobsRuleset.maxShuntingStorageTracks + 1);
			List<Track> destinationTracks = new List<Track>();
			do
			{
				destinationTracks.Clear();
				for (int i = 0; i < countTracks; i++)
				{
					Track track = yto.GetTrackThatHasEnoughFreeSpace(
						startingStation.logicStation.yard.StorageTracks.Except(destinationTracks).ToList(),
						approxTrainLength / (float)countTracks);
					if (track == null)
					{
						break;
					}
					destinationTracks.Add(track);
				}
			} while (destinationTracks.Count < countTracks--);
			if (destinationTracks.Count == 0)
			{
				Debug.LogWarning(string.Format(
					"Could not create ChainJob[{0}]: {1} - {2}. Found no StorageTrack with enough free space!",
					JobType.ShuntingUnload,
					startingStation.logicStation.ID,
					destStation.logicStation.ID));
				return null;
			}

			// divide trainCars between destination tracks
			int countCarsPerTrainset = trainCars.Count / destinationTracks.Count;
			int countTrainsetsWithExtraCar = trainCars.Count % destinationTracks.Count;
			List<TrainCar> orderedTrainCars = new List<TrainCar>();
			List<CarsPerTrack> carsPerDestinationTrack = new List<CarsPerTrack>();
			for (int i = 0; i < destinationTracks.Count; i++)
			{
				int rangeStart = i * countCarsPerTrainset + Math.Min(i, countTrainsetsWithExtraCar);
				int rangeCount = i < countTrainsetsWithExtraCar ? countCarsPerTrainset + 1 : countCarsPerTrainset;
				Track destinationTrack = destinationTracks[i];
				carsPerDestinationTrack.Add(
					new CarsPerTrack(
						destinationTrack,
						(from car in trainCars.GetRange(rangeStart, rangeCount) select car.logicCar).ToList()));
			}

			float bonusTimeLimit;
			float initialWage;
			Utilities.CalculateShuntingBonusTimeLimitAndWage(
				JobType.ShuntingLoad,
				destinationTracks.Count,
				(from tc in trainCars select tc.carType).ToList<TrainCarType>(),
				transportedCargoPerCar,
				out bonusTimeLimit,
				out initialWage
			);
			JobLicenses requiredLicenses = LicenseManager.GetRequiredLicensesForJobType(JobType.Transport)
				| LicenseManager.GetRequiredLicensesForCarContainerTypes(carContainerTypes)
				| LicenseManager.GetRequiredLicenseForNumberOfTransportedCars(trainCars.Count);
			return GenerateShuntingUnloadChainController(
				startingStation,
				startingTrack,
				loadMachine,
				destStation,
				carsPerDestinationTrack,
				trainCars,
				transportedCargoPerCar,
				Enumerable.Repeat(1.0f, trainCars.Count).ToList(),
				forceCorrectCargoStateOnCars,
				bonusTimeLimit,
				initialWage,
				requiredLicenses);
		}

		private static JobChainController GenerateShuntingUnloadChainController(
			StationController startingStation,
			Track startingTrack,
			WarehouseMachine unloadMachine,
			StationController destStation,
			List<CarsPerTrack> carsPerDestinationTrack,
			List<TrainCar> orderedTrainCars,
			List<CargoType> orderedCargoTypes,
			List<float> orderedCargoAmounts,
			bool forceCorrectCargoStateOnCars,
			float bonusTimeLimit,
			float initialWage,
			JobLicenses requiredLicenses)
		{
			GameObject gameObject = new GameObject(string.Format(
				"ChainJob[{0}]: {1} - {2}",
				JobType.ShuntingUnload,
				startingStation.logicStation.ID,
				destStation.logicStation.ID
			));
			gameObject.transform.SetParent(startingStation.transform);
			JobChainController jobChainController
				= new JobChainController(gameObject);
			StationsChainData chainData = new StationsChainData(
				startingStation.stationInfo.YardID,
				destStation.stationInfo.YardID);
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
			List<CarsPerCargoType> unloadData = cargoTypeToTrainCarAndAmount.Select(
				kvPair => new CarsPerCargoType(
					kvPair.Key,
					kvPair.Value.Select(t => t.Item1.logicCar).ToList(),
					kvPair.Value.Aggregate(0.0f, (sum, t) => sum + t.Item2))).ToList();
			StaticShuntingUnloadJobDefinition staticShuntingUnloadJobDefinition
				= gameObject.AddComponent<StaticShuntingUnloadJobDefinition>();
			staticShuntingUnloadJobDefinition.PopulateBaseJobDefinition(
				startingStation.logicStation,
				bonusTimeLimit,
				initialWage,
				chainData,
				requiredLicenses);
			staticShuntingUnloadJobDefinition.startingTrack = startingTrack;
			staticShuntingUnloadJobDefinition.carsPerDestinationTrack = carsPerDestinationTrack;
			staticShuntingUnloadJobDefinition.unloadData = unloadData;
			staticShuntingUnloadJobDefinition.unloadMachine = unloadMachine;
			staticShuntingUnloadJobDefinition.forceCorrectCargoStateOnCars = forceCorrectCargoStateOnCars;
			jobChainController.AddJobDefinitionToChain(staticShuntingUnloadJobDefinition);
			return jobChainController;
		}
	}
}
