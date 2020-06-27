﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using DV.Logic.Job;

namespace PersistentJobsMod
{
	class ShuntingLoadJobProceduralGenerator
	{
		public static JobChainControllerWithEmptyHaulGeneration GenerateShuntingLoadJobWithCarSpawning(
			StationController startingStation,
			bool forceLicenseReqs,
			System.Random rng)
		{
			Debug.Log("[PersistentJobs] load: generating with car spawning");
			YardTracksOrganizer yto = YardTracksOrganizer.Instance;
			List<CargoGroup> availableCargoGroups = startingStation.proceduralJobsRuleset.outputCargoGroups;
			int countTrainCars = rng.Next(
				startingStation.proceduralJobsRuleset.minCarsPerJob,
				startingStation.proceduralJobsRuleset.maxCarsPerJob);

			if (forceLicenseReqs)
			{
				Debug.Log("[PersistentJobs] load: forcing license requirements");
				if (!LicenseManager.IsJobLicenseAcquired(JobLicenses.Shunting))
				{
					Debug.LogError("Trying to generate a ShuntingLoad job with forceLicenseReqs=true should " +
						"never happen if player doesn't have Shunting license!");
					return null;
				}
				availableCargoGroups
					= (from cg in availableCargoGroups
					   where LicenseManager.IsLicensedForJob(cg.CargoRequiredLicenses)
					   select cg).ToList();
				countTrainCars = Math.Min(countTrainCars, LicenseManager.GetMaxNumberOfCarsPerJobWithAcquiredJobLicenses());
			}

			CargoGroup chosenCargoGroup = Utilities.GetRandomFromEnumerable(availableCargoGroups, rng);

			// choose cargo & trainCar types
			Debug.Log("[PersistentJobs] load: choosing cargo & trainCar types");
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

			// choose starting tracks
			int countTracks = rng.Next(1, startingStation.proceduralJobsRuleset.maxShuntingStorageTracks + 1);
			Debug.Log(string.Format("[PersistentJobs] load: choosing {0} starting tracks", countTracks));
			int countCarsPerTrainset = countTrainCars / countTracks;
			int countTrainsetsWithExtraCar = countTrainCars % countTracks;
			List<Track> tracks = new List<Track>();
			do
			{
				tracks.Clear();
				for (int i = 0; i < countTracks; i++)
				{
					int rangeStart = i * countCarsPerTrainset + Math.Min(i, countTrainsetsWithExtraCar);
					int rangeCount = i < countTrainsetsWithExtraCar ? countCarsPerTrainset + 1 : countCarsPerTrainset;
					List<TrainCarType> trainCarTypesPerTrack = orderedTrainCarTypes.GetRange(rangeStart, rangeCount);
					float approxTrainLengthPerTrack = yto.GetTotalCarTypesLength(trainCarTypesPerTrack)
						+ yto.GetSeparationLengthBetweenCars(trainCarTypesPerTrack.Count);
					Track track = yto.GetTrackThatHasEnoughFreeSpace(
						startingStation.logicStation.yard.StorageTracks.Except(tracks).ToList(),
						approxTrainLengthPerTrack / (float)countTracks);
					if (track == null)
					{
						break;
					}
					tracks.Add(track);
				}
			} while (tracks.Count < countTracks--);
			if (tracks.Count == 0)
			{
				Debug.LogWarning("[PersistentJobs] load: Couldn't find startingTrack with enough free space for train!");
				return null;
			}

			// choose random destination station that has at least 1 available track
			Debug.Log("[PersistentJobs] load: choosing destination");
			float approxTrainLength = yto.GetTotalCarTypesLength(orderedTrainCarTypes)
				+ yto.GetSeparationLengthBetweenCars(countTrainCars);
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
				Debug.LogWarning("Couldn't find a station with enough free space for train!");
				return null;
			}

			// spawn trainCars & form carsPerStartingTrack
			Debug.Log("[PersistentJobs] load: spawning trainCars");
			List<TrainCar> orderedTrainCars = new List<TrainCar>();
			List<CarsPerTrack> carsPerStartingTrack = new List<CarsPerTrack>();
			for (int i = 0; i < tracks.Count; i++)
			{
				int rangeStart = i * countCarsPerTrainset + Math.Min(i, countTrainsetsWithExtraCar);
				int rangeCount = i < countTrainsetsWithExtraCar ? countCarsPerTrainset + 1 : countCarsPerTrainset;
				Debug.Log(string.Format(
					"[PersistentJobs] load: spawning cars in range [{0}-{1}) from total range [0-{2})",
					rangeStart,
					rangeStart + rangeCount,
					orderedTrainCarTypes.Count));
				Track startingTrack = tracks[i];
				RailTrack railTrack = SingletonBehaviour<LogicController>.Instance.LogicToRailTrack[startingTrack];
				List<TrainCar> spawnedCars = CarSpawner.SpawnCarTypesOnTrack(
					orderedTrainCarTypes.GetRange(rangeStart, rangeCount),
					railTrack,
					true,
					0.0,
					false,
					true);
				if (spawnedCars == null)
				{
					Debug.LogWarning("[PersistentJobs] load: Failed to spawn some trainCars!");
					SingletonBehaviour<CarSpawner>.Instance.DeleteTrainCars(orderedTrainCars, true);
					return null;
				}
				orderedTrainCars.AddRange(spawnedCars);
				carsPerStartingTrack.Add(
					new CarsPerTrack(startingTrack, (from car in spawnedCars select car.logicCar).ToList()));
			}

			JobChainControllerWithEmptyHaulGeneration jcc = GenerateShuntingLoadJobWithExistingCars(
				startingStation,
				carsPerStartingTrack,
				destStation,
				orderedTrainCars,
				orderedCargoTypes,
				rng,
				true);

			if (jcc == null)
			{
				Debug.LogWarning("[PersistentJobs] load: Couldn't generate job chain. Deleting spawned trainCars!");
				SingletonBehaviour<CarSpawner>.Instance.DeleteTrainCars(orderedTrainCars, true);
				return null;
			}

			return jcc;
		}

		public static JobChainControllerWithEmptyHaulGeneration GenerateShuntingLoadJobWithExistingCars(
			StationController startingStation,
			List<CarsPerTrack> carsPerStartingTrack,
			StationController destStation,
			List<TrainCar> trainCars,
			List<CargoType> transportedCargoPerCar,
			System.Random rng,
			bool forceCorrectCargoStateOnCars = false)
		{
			Debug.Log("[PersistentJobs] load: generating with pre-spawned cars");
			YardTracksOrganizer yto = YardTracksOrganizer.Instance;
			HashSet<CargoContainerType> carContainerTypes = new HashSet<CargoContainerType>();
			foreach (TrainCar trainCar in trainCars)
			{
				carContainerTypes.Add(CargoTypes.CarTypeToContainerType[trainCar.carType]);
			}
			float approxTrainLength = yto.GetTotalTrainCarsLength(trainCars)
				+ yto.GetSeparationLengthBetweenCars(trainCars.Count);

			// choose warehosue machine
			Debug.Log("[PersistentJobs] load: choosing warehouse machine");
			List<WarehouseMachineController> supportedWMCs = startingStation.warehouseMachineControllers
					.Where(wm => wm.supportedCargoTypes.Intersect(transportedCargoPerCar).Count() > 0)
					.ToList();
			if (supportedWMCs.Count == 0)
			{
				Debug.LogWarning(string.Format(
					"[PersistentJobs] load: Could not create ChainJob[{0}]: {1} - {2}. Found no supported WarehouseMachine!",
					JobType.ShuntingLoad,
					startingStation.logicStation.ID,
					destStation.logicStation.ID
				));
				return null;
			}
			WarehouseMachine loadMachine = Utilities.GetRandomFromEnumerable(supportedWMCs, rng).warehouseMachine;

			// choose destination track
			Debug.Log("[PersistentJobs] load: choosing destination track");
			Track destinationTrack = yto.GetTrackThatHasEnoughFreeSpace(
				yto.FilterOutOccupiedTracks(startingStation.logicStation.yard.TransferOutTracks),
				approxTrainLength
			);
			if (destinationTrack == null)
			{
				destinationTrack = yto.GetTrackThatHasEnoughFreeSpace(
					startingStation.logicStation.yard.TransferOutTracks,
					approxTrainLength
				);
			}
			if (destinationTrack == null)
			{
				Debug.LogWarning(string.Format(
					"[PersistentJobs] load: Could not create ChainJob[{0}]: {1} - {2}. " +
					"Found no TransferOutTrack with enough free space!",
					JobType.ShuntingLoad,
					startingStation.logicStation.ID,
					destStation.logicStation.ID
				));
				return null;
			}

			Debug.Log("[PersistentJobs] load: calculating time/wage/licenses");
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
				| LicenseManager.GetRequiredLicensesForCarContainerTypes(carContainerTypes)
				| LicenseManager.GetRequiredLicenseForNumberOfTransportedCars(trainCars.Count);
			return GenerateShuntingLoadChainController(
				startingStation,
				carsPerStartingTrack,
				loadMachine,
				destStation,
				destinationTrack,
				trainCars,
				transportedCargoPerCar,
				Enumerable.Repeat(1.0f, trainCars.Count).ToList(),
				forceCorrectCargoStateOnCars,
				bonusTimeLimit,
				initialWage,
				requiredLicenses
			);
		}

		private static JobChainControllerWithEmptyHaulGeneration GenerateShuntingLoadChainController(
			StationController startingStation,
			List<CarsPerTrack> carsPerStartingTrack,
			WarehouseMachine loadMachine,
			StationController destStation,
			Track destinationTrack,
			List<TrainCar> orderedTrainCars,
			List<CargoType> orderedCargoTypes,
			List<float> orderedCargoAmounts,
			bool forceCorrectCargoStateOnCars,
			float bonusTimeLimit,
			float initialWage,
			JobLicenses requiredLicenses)
		{
			Debug.Log(string.Format(
				"[PersistentJobs] load: attempting to generate ChainJob[{0}]: {1} - {2}",
				JobType.ShuntingLoad,
				startingStation.logicStation.ID,
				destStation.logicStation.ID
			));
			GameObject gameObject = new GameObject(string.Format(
				"ChainJob[{0}]: {1} - {2}",
				JobType.ShuntingLoad,
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
			staticShuntingLoadJobDefinition.forceCorrectCargoStateOnCars = forceCorrectCargoStateOnCars;
			jobChainController.AddJobDefinitionToChain(staticShuntingLoadJobDefinition);
			return jobChainController;
		}

		public static Dictionary<Trainset, List<TrainCar>> GroupTrainCarsByTrainset(List<TrainCar> trainCars)
		{
			Dictionary<Trainset, List<TrainCar>> trainCarsPerTrainSet = new Dictionary<Trainset, List<TrainCar>>();
			foreach (TrainCar tc in trainCars)
			{
				// TODO: to skip player spawned cars or to not?
				if (tc != null)
				{
					if (!trainCarsPerTrainSet.ContainsKey(tc.trainset))
					{
						trainCarsPerTrainSet.Add(tc.trainset, new List<TrainCar>());
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
			IEnumerable<StationController> stationControllers
				= SingletonBehaviour<LogicController>.Instance.YardIdToStationController.Values;
			Dictionary<StationController, List<(List<TrainCar>, List<CargoGroup>)>> cgsPerTcsPerSc
						= new Dictionary<StationController, List<(List<TrainCar>, List<CargoGroup>)>>();
			float abandonmentThreshold = 1.2f * Main.DVJobDestroyDistanceRegular;
			Debug.Log(string.Format(
				"[PersistentJobs] station grouping: # of trainSets: {0}, # of stations: {1}",
				trainCarsPerTrainSet.Values.Count,
				stationControllers.Count()));
			foreach (List<TrainCar> tcs in trainCarsPerTrainSet.Values)
			{
				SortedList<float, StationController> stationsByDistance
					= new SortedList<float, StationController>();
				foreach (StationController sc in stationControllers)
				{
					// since all trainCars in the trainset are coupled,
					// use the position of the first one to approximate the position of the trainset
					Vector3 trainPosition = tcs[0].gameObject.transform.position;
					Vector3 stationPosition = sc.gameObject.transform.position;
					float distance = (trainPosition - stationPosition).sqrMagnitude;
					/*Debug.Log(string.Format(
						"[PersistentJobs] station grouping: train position {0}, station position {1}, " +
						"distance {2:F}, threshold {3:F}",
						trainPosition,
						stationPosition,
						distance,
						abandonmentThreshold));*/
					// only create jobs for trainCars within a reasonable range of a station
					if (distance < abandonmentThreshold)
					{
						stationsByDistance.Add(distance, sc);
					}
				}
				if (stationsByDistance.Count == 0)
				{
					// trainCars not near any station; abandon them
					Debug.Log("[PersistentJobs] station grouping: train not near any station; abandoning train");
					continue;
				}
				// the first station is the closest
				KeyValuePair<float, StationController> closestStation = stationsByDistance.ElementAt(0);
				if (!cgsPerTcsPerSc.ContainsKey(closestStation.Value))
				{
					cgsPerTcsPerSc.Add(closestStation.Value, new List<(List<TrainCar>, List<CargoGroup>)>());
				}
				Debug.Log(string.Format(
					"[PersistentJobs] station grouping: assigning train to {0} with distance {1:F}",
					closestStation.Value,
					closestStation.Key));
				cgsPerTcsPerSc[closestStation.Value].Add((tcs, new List<CargoGroup>()));
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
						else if ((isFulfillingLicenseReqs &&
									(licensedCargoGroups.Count == 0 ||
										(cargoGroupsToUse.Count() > 0 &&
											cargoGroupsToUse.Intersect(licensedCargoGroups).Count() == 0) ||
										trainCarsToLoad.Count + trainCarsToAdd.Count <= maxCarsLicensed)) ||
								(cargoGroupsToUse.Count() > 0 &&
									cargoGroupsToUse.Intersect(availableCargoGroups).Count() == 0))
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
						cargoGroupsToUse = cargoGroupsToUse.Count() > 0
							? cargoGroupsToUse.Intersect(availableCargoGroups)
							: availableCargoGroups;
						trainCarsToLoad.AddRange(trainCarsToAdd);
						cgsPerTcs.RemoveAt(cgsPerTcs.Count - 1);
						countTracks--;
					}

					if (trainCarsToLoad.Count == 0 || cargoGroupsToUse.Count() == 0)
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
						carsPerTrackDict.Select(
							kvPair => new CarsPerTrack(kvPair.Key, kvPair.Value)).ToList(),
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

		public static IEnumerable<JobChainController> doJobGeneration(
			List<(StationController, List<CarsPerTrack>, StationController, List<TrainCar>, List<CargoType>)> jobInfos,
			System.Random rng,
			bool forceCorrectCargoStateOnCars = true)
		{
			return jobInfos.Select((definition) =>
			{
				// I miss having a spread operator :(
				(StationController ss, List<CarsPerTrack> cpst, StationController ds, _, _) = definition;
				(_, _, _, List<TrainCar> tcs, List<CargoType> cts) = definition;

				return (JobChainController)GenerateShuntingLoadJobWithExistingCars(
					ss,
					cpst,
					ds,
					tcs,
					cts,
					rng,
					forceCorrectCargoStateOnCars);
			});
		}
	}
}
