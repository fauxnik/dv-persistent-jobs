using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using DV.Logic.Job;

namespace PersistentJobsMod
{
	class JobProceduralGenerationUtilities
	{
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
						cgsPerTcs.Item2.Clear();
					}

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

		public static void PopulateCargoGroupsPerLoadedTrainCarSet(
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
						cgsPerTcs.Item2.Clear();
					}

					// transport jobs
					foreach (CargoGroup cg in sc.proceduralJobsRuleset.outputCargoGroups)
					{
						// ensure all trainCars are loaded with a cargoType from the cargoGroup
						if (cgsPerTcs.Item1.All(tc => cg.cargoTypes.Contains(tc.logicCar.CurrentCargoTypeInCar)))
						{
							cgsPerTcs.Item2.Add(cg);
						}
					}

					// it shouldn't happen that both input and output cargo groups match loaded cargo
					// but, just in case, skip trying input groups if any output groups have been found
					if (cgsPerTcs.Item2.Count > 0)
					{
						continue;
					}

					// shunting unload jobs
					foreach (CargoGroup cg in sc.proceduralJobsRuleset.inputCargoGroups)
					{
						// ensure all trainCars are loaded with a cargoType from the cargoGroup
						if (cgsPerTcs.Item1.All(tc => cg.cargoTypes.Contains(tc.logicCar.CurrentCargoTypeInCar)))
						{
							cgsPerTcs.Item2.Add(cg);
						}
					}
				}
			}
		}
	}
}
