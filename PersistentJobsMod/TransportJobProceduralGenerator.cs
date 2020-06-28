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
		public static JobChainControllerWithEmptyHaulGeneration GenerateTransportJobWithCarSpawning(
			StationController startingStation,
			bool forceLicenseReqs,
			System.Random rng)
		{
			Debug.Log("[PersistentJobs] transport: generating with car spawning");
			YardTracksOrganizer yto = YardTracksOrganizer.Instance;
			List<CargoGroup> availableCargoGroups = startingStation.proceduralJobsRuleset.outputCargoGroups;
			int countTrainCars = rng.Next(
				startingStation.proceduralJobsRuleset.minCarsPerJob,
				startingStation.proceduralJobsRuleset.maxCarsPerJob);

			if (forceLicenseReqs)
			{
				Debug.Log("[PersistentJobs] transport: forcing license requirements");
				if (!LicenseManager.IsJobLicenseAcquired(JobLicenses.FreightHaul))
				{
					Debug.LogError("[PersistentJobs] transport: Trying to generate a Transport job with " +
						"forceLicenseReqs=true should never happen if player doesn't have FreightHaul license!");
					return null;
				}
				availableCargoGroups
					= (from cg in availableCargoGroups
					   where LicenseManager.IsLicensedForJob(cg.CargoRequiredLicenses)
					   select cg).ToList();
				countTrainCars = Math.Min(countTrainCars, LicenseManager.GetMaxNumberOfCarsPerJobWithAcquiredJobLicenses());
			}
			if (availableCargoGroups.Count == 0)
			{
				Debug.LogWarning("[PersistentJobs] transport: no available cargo groups");
				return null;
			}

			CargoGroup chosenCargoGroup = Utilities.GetRandomFromEnumerable(availableCargoGroups, rng);

			// choose cargo & trainCar types
			Debug.Log("[PersistentJobs] transport: choosing cargo & trainCar types");
			List<CargoType> availableCargoTypes = chosenCargoGroup.cargoTypes;
			List<CargoType> orderedCargoTypes = new List<CargoType>();
			List<TrainCarType> orderedTrainCarTypes = new List<TrainCarType>();
			for (int i = 0; i < countTrainCars; i++)
			{
				CargoType chosenCargoType = Utilities.GetRandomFromEnumerable(availableCargoTypes, rng);
				List<CargoContainerType> availableContainers
					= CargoTypes.GetCarContainerTypesThatSupportCargoType(chosenCargoType);
				CargoContainerType chosenContainerType = Utilities.GetRandomFromEnumerable(availableContainers, rng);
				List<TrainCarType> availableTrainCarTypes
					= CargoTypes.GetTrainCarTypesThatAreSpecificContainerType(chosenContainerType);
				TrainCarType chosenTrainCarType = Utilities.GetRandomFromEnumerable(availableTrainCarTypes, rng);
				orderedCargoTypes.Add(chosenCargoType);
				orderedTrainCarTypes.Add(chosenTrainCarType);
			}
			float approxTrainLength = yto.GetTotalCarTypesLength(orderedTrainCarTypes)
				+ yto.GetSeparationLengthBetweenCars(countTrainCars);

			// choose starting track
			Debug.Log("[PersistentJobs] transport: choosing starting track");
			Track startingTrack
				= yto.GetTrackThatHasEnoughFreeSpace(startingStation.logicStation.yard.TransferOutTracks, approxTrainLength);
			if (startingTrack == null)
			{
				Debug.LogWarning("[PersistentJobs] transport: Couldn't find startingTrack with enough free space for train!");
				return null;
			}

			// choose random destination station that has at least 1 available track
			Debug.Log("[PersistentJobs] transport: choosing destination");
			List<StationController> availableDestinations = new List<StationController>(chosenCargoGroup.stations);
			StationController destStation = null;
			Track destinationTrack = null;
			while (availableDestinations.Count > 0 && destinationTrack == null)
			{
				destStation = Utilities.GetRandomFromEnumerable(availableDestinations, rng);
				availableDestinations.Remove(destStation);
				destinationTrack = yto.GetTrackThatHasEnoughFreeSpace(
					yto.FilterOutOccupiedTracks(destStation.logicStation.yard.TransferInTracks),
					approxTrainLength);
			}
			if (destinationTrack == null)
			{
				Debug.LogWarning("[PersistentJobs] transport: Couldn't find a station with enough free space for train!");
				return null;
			}

			// spawn trainCars
			Debug.Log("[PersistentJobs] transport: spawning trainCars");
			RailTrack railTrack = SingletonBehaviour<LogicController>.Instance.LogicToRailTrack[startingTrack];
			List<TrainCar> orderedTrainCars = CarSpawner.SpawnCarTypesOnTrack(
				orderedTrainCarTypes,
				railTrack,
				true,
				0.0,
				false,
				true);
			if (orderedTrainCars == null)
			{
				Debug.LogWarning("[PersistentJobs] transport: Failed to spawn trainCars!");
				return null;
			}

			JobChainControllerWithEmptyHaulGeneration jcc = GenerateTransportJobWithExistingCars(
				startingStation,
				startingTrack,
				destStation,
				orderedTrainCars,
				orderedCargoTypes,
				Enumerable.Repeat(1.0f, countTrainCars).ToList(),
				rng,
				true);

			if (jcc == null)
			{
				Debug.LogWarning("[PersistentJobs] transport: Couldn't generate job chain. Deleting spawned trainCars!");
				SingletonBehaviour<CarSpawner>.Instance.DeleteTrainCars(orderedTrainCars, true);
				return null;
			}

			return jcc;
		}

		public static JobChainControllerWithEmptyHaulGeneration GenerateTransportJobWithExistingCars(
			StationController startingStation,
			Track startingTrack,
			StationController destStation,
			List<TrainCar> trainCars,
			List<CargoType> transportedCargoPerCar,
			List<float> cargoAmountPerCar,
			System.Random rng,
			bool forceCorrectCargoStateOnCars = false)
		{
			Debug.Log("[PersistentJobs] transport: generating with pre-spawned cars");
			YardTracksOrganizer yto = YardTracksOrganizer.Instance;
			HashSet<CargoContainerType> carContainerTypes = new HashSet<CargoContainerType>();
			foreach (TrainCar trainCar in trainCars)
			{
				carContainerTypes.Add(CargoTypes.CarTypeToContainerType[trainCar.carType]);
			}

			Debug.Log("[PersistentJobs] transport: choosing destination track");
			float approxTrainLength = yto.GetTotalTrainCarsLength(trainCars)
				+ yto.GetSeparationLengthBetweenCars(trainCars.Count);
			Track destinationTrack = yto.GetTrackThatHasEnoughFreeSpace(
				yto.FilterOutOccupiedTracks(destStation.logicStation.yard.TransferInTracks),
				approxTrainLength);
			if (destinationTrack == null)
			{
				destinationTrack = yto.GetTrackThatHasEnoughFreeSpace(
					destStation.logicStation.yard.TransferInTracks,
					approxTrainLength);
			}
			if (destinationTrack == null)
			{
				Debug.LogWarning(string.Format(
					"[PersistentJobs] transport: Could not create ChainJob[{0}]: {1} - {2}. " +
					"Found no TransferInTrack with enough free space!",
					JobType.Transport,
					startingStation.logicStation.ID,
					destStation.logicStation.ID
				));
				return null;
			}
			List<TrainCarType> transportedCarTypes = (from tc in trainCars select tc.carType)
				.ToList<TrainCarType>();

			Debug.Log("[PersistentJobs] transport: calculating time/wage/licenses");
			float bonusTimeLimit;
			float initialWage;
			Utilities.CalculateTransportBonusTimeLimitAndWage(
				JobType.Transport,
				startingStation,
				destStation,
				transportedCarTypes,
				transportedCargoPerCar,
				out bonusTimeLimit,
				out initialWage
			);
			JobLicenses requiredLicenses = LicenseManager.GetRequiredLicensesForJobType(JobType.Transport)
				| LicenseManager.GetRequiredLicensesForCargoTypes(transportedCargoPerCar)
				| LicenseManager.GetRequiredLicenseForNumberOfTransportedCars(trainCars.Count);
			return TransportJobProceduralGenerator.GenerateTransportChainController(
				startingStation,
				startingTrack,
				destStation,
				destinationTrack,
				trainCars,
				transportedCargoPerCar,
				cargoAmountPerCar,
				forceCorrectCargoStateOnCars,
				bonusTimeLimit,
				initialWage,
				requiredLicenses
			);
		}

		private static JobChainControllerWithEmptyHaulGeneration GenerateTransportChainController(
			StationController startingStation,
			Track startingTrack,
			StationController destStation,
			Track destTrack,
			List<TrainCar> orderedTrainCars,
			List<CargoType> orderedCargoTypes,
			List<float> orderedCargoAmounts,
			bool forceCorrectCargoStateOnCars,
			float bonusTimeLimit,
			float initialWage,
			JobLicenses requiredLicenses)
		{
			Debug.Log(string.Format(
				"[PersistentJobs] transport: attempting to generate ChainJob[{0}]: {1} - {2}",
				JobType.ShuntingLoad,
				startingStation.logicStation.ID,
				destStation.logicStation.ID
			));
			GameObject gameObject = new GameObject(string.Format(
				"ChainJob[{0}]: {1} - {2}",
				JobType.Transport,
				startingStation.logicStation.ID,
				destStation.logicStation.ID
			));
			gameObject.transform.SetParent(startingStation.transform);
			JobChainControllerWithEmptyHaulGeneration jobChainController
				= new JobChainControllerWithEmptyHaulGeneration(gameObject);
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
			staticTransportJobDefinition.forceCorrectCargoStateOnCars = forceCorrectCargoStateOnCars;
			jobChainController.AddJobDefinitionToChain(staticTransportJobDefinition);
			return jobChainController;
		}
	}
}
