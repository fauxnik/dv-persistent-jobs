using System;
using System.Collections.Generic;
using System.Linq;
using Harmony12;
using UnityEngine;
using DV.Logic.Job;

namespace PersistentJobsMod
{
    class Utilities
    {
		// taken from JobChainControllerWithEmptyHaulGeneration.ExtractCorrespondingTrainCars
		public static List<TrainCar> ExtractCorrespondingTrainCars(JobChainController context, List<Car> logicCars)
		{
			if (logicCars == null || logicCars.Count == 0)
			{
				return null;
			}
			List<TrainCar> list = new List<TrainCar>();
			for (int i = 0; i < logicCars.Count; i++)
			{
				for (int j = 0; j < context.trainCarsForJobChain.Count; j++)
				{
					if (context.trainCarsForJobChain[j].logicCar == logicCars[i])
					{
						list.Add(context.trainCarsForJobChain[j]);
						break;
					}
				}
			}
			if (list.Count != logicCars.Count)
			{
				return null;
			}
			return list;
		}

		// based off EmptyHaulJobProceduralGenerator.CalculateBonusTimeLimitAndWage
		public static void CalculateTransportBonusTimeLimitAndWage(
			StationController startingStation,
			StationController destStation,
			List<TrainCarType> transportedCarTypes,
			List<CargoType> transportedCargoTypes,
			out float bonusTimeLimit,
			out float initialWage)
		{
			float distanceBetweenStations
				= JobPaymentCalculator.GetDistanceBetweenStations(startingStation, destStation);
			bonusTimeLimit = JobPaymentCalculator.CalculateHaulBonusTimeLimit(distanceBetweenStations, false);
			initialWage = JobPaymentCalculator.CalculateJobPayment(
				JobType.EmptyHaul,
				distanceBetweenStations,
				Utilities.ExtractTransportPaymentCalculationData(transportedCarTypes, transportedCargoTypes)
			);
		}

		// based off EmptyHaulJobProceduralGenerator.ExtractEmptyHaulPaymentCalculationData
		private static PaymentCalculationData ExtractTransportPaymentCalculationData(
			List<TrainCarType> orderedCarTypes,
			List<CargoType> orderedCargoTypes)
		{
			if (orderedCarTypes == null)
			{
				return null;
			}
			Dictionary<TrainCarType, int> trainCarTypeToCount = new Dictionary<TrainCarType, int>();
			foreach (TrainCarType trainCarType in orderedCarTypes)
			{
				if (!trainCarTypeToCount.ContainsKey(trainCarType))
				{
					trainCarTypeToCount[trainCarType] = 0;
				}
				trainCarTypeToCount[trainCarType] += 1;
			}
			Dictionary<CargoType, int> cargoTypeToCount = new Dictionary<CargoType, int>();
			foreach (CargoType cargoType in orderedCargoTypes)
            {
				if (!cargoTypeToCount.ContainsKey(cargoType))
                {
					cargoTypeToCount[cargoType] = 0;
                }
				cargoTypeToCount[cargoType] += 1;
            }
			return new PaymentCalculationData(trainCarTypeToCount, cargoTypeToCount);
		}

		public static List<CargoType> GetCargoTypesForCarType(TrainCarType trainCarType)
        {
			CargoContainerType containerType = CargoTypes.CarTypeToContainerType[trainCarType];
			List<CargoType> cargoTypes = new List<CargoType>();
			Dictionary<CargoType, List<CargoContainerType>> cargoTypeToSupportedContainerTypes
				= Traverse.Create(typeof(CargoTypes))
					.Field("cargoTypeToSupportedCarContainer")
					.GetValue<Dictionary<CargoType, List<CargoContainerType>>>();
			foreach (CargoType cargoType in Enum.GetValues(typeof(CargoType)).Cast<CargoType>().ToList<CargoType>())
            {
				if (cargoTypeToSupportedContainerTypes[cargoType].Contains(containerType))
                {
					cargoTypes.Add(cargoType);
                }
            }
			return cargoTypes;
        }

		// based on StationProceduralJobGenerator.GenerateBaseCargoTrainData
		public static (
			List<CarTypesPerCargoType>,
			List<TrainCarType>,
			CargoGroup
		) GenerateBaseCargoTrainData(
			int minCountCars,
			int maxCountCars,
			List<CargoGroup> availableCargoGroups,
			System.Random rng)
		{
			List<CarTypesPerCargoType> carTypesPerCargoTypes = new List<CarTypesPerCargoType>();
			List<TrainCarType> allCarTypes = new List<TrainCarType>();
			int countCarsInTrain = rng.Next(minCountCars, maxCountCars + 1);
			CargoGroup pickedCargoGroup = Utilities.GetRandomFromList<CargoGroup>(availableCargoGroups, rng);
			List<CargoType> pickedCargoTypes = pickedCargoGroup.cargoTypes;
			pickedCargoTypes = Utilities.GetMultipleRandomsFromList<CargoType>(
				pickedCargoTypes,
				Math.Min(countCarsInTrain, rng.Next(1, pickedCargoTypes.Count + 1)),
				rng
			);
			int countCargoTypes = pickedCargoTypes.Count;
			int countCarsPerCargoType = countCarsInTrain / countCargoTypes;
			int countCargoTypesWithExtraCar = countCarsInTrain % countCargoTypes;
			for (int i = 0; i < countCargoTypes; i++)
			{
				int countCars = i < countCargoTypesWithExtraCar ? countCarsPerCargoType + 1 : countCarsPerCargoType;
				List<CargoContainerType> cargoContainerTypesThatSupportCargoType
					= CargoTypes.GetCarContainerTypesThatSupportCargoType(pickedCargoTypes[i]);
				List<TrainCarType> trainCarTypesThatAreSpecificContainerType
					= CargoTypes.GetTrainCarTypesThatAreSpecificContainerType(
						Utilities.GetRandomFromList<CargoContainerType>(cargoContainerTypesThatSupportCargoType, rng)
					);
				List<TrainCarType> trainCarTypes = new List<TrainCarType>();
				for (int j = 0; j < countCars; j++)
				{
					trainCarTypes.Add(
						Utilities.GetRandomFromList<TrainCarType>(trainCarTypesThatAreSpecificContainerType, rng)
					);
				}
				carTypesPerCargoTypes
					.Add(new CarTypesPerCargoType(trainCarTypes, pickedCargoTypes[i], (float)trainCarTypes.Count));
				allCarTypes.AddRange(trainCarTypes);
			}
			return (carTypesPerCargoTypes, allCarTypes, pickedCargoGroup);
		}

		public static (
			List<CargoType>,
			CargoGroup
		) GenerateCargoTypesForExistingCars(
			List<TrainCar> orderedTrainCars,
			List<CargoGroup> availableCargoGroups,
			System.Random rng)
		{
			List<CarsPerCargoType> carsPerCargoTypes = new List<CarsPerCargoType>();
			List<List<CargoType>> orderedCargoTypesPerTrainCar
				= (from tc in orderedTrainCars select GetCargoTypesForCarType(tc.carType)).ToList();
			// find cargo groups that satisfy at least one cargo type for every train car
			List<CargoGroup> filteredCargoGroups = availableCargoGroups
				.Where(cg => orderedCargoTypesPerTrainCar.All(cts => cts.Intersect(cg.cargoTypes).Count() > 0))
				.ToList();
			if (filteredCargoGroups.Count == 0)
            {
				return (null, null);
            }
			CargoGroup pickedCargoGroup = Utilities.GetRandomFromList<CargoGroup>(filteredCargoGroups, rng);
			List<CargoType> pickedCargoTypes = pickedCargoGroup.cargoTypes;
			List<CargoType> orderedCargoTypes = orderedCargoTypesPerTrainCar.Select(
				cts => GetRandomFromList<CargoType>(cts.Intersect(pickedCargoTypes).ToList(), rng)
			).ToList();
			return (orderedCargoTypes, pickedCargoGroup);
		}

		// taken from StationProcedurationJobGenerator.GetRandomFromList
		public static T GetRandomFromList<T>(List<T> list, System.Random rng)
        {
			return list[rng.Next(0, list.Count)];
        }

		// taken from StationProcedurationJobGenerator.GetMultipleRandomsFromList
		public static List<T> GetMultipleRandomsFromList<T>(List<T> list, int countToGet, System.Random rng)
        {
			List<T> list2 = new List<T>(list);
			if (countToGet > list2.Count)
			{
				Debug.LogError("Trying to get more random items from list than it contains. Returning all items from list.");
				return list2;
			}
			List<T> list3 = new List<T>();
			for (int i = 0; i < countToGet; i++)
			{
				int index = rng.Next(0, list2.Count);
				list3.Add(list2[index]);
				list2.RemoveAt(index);
			}
			return list3;
		}
	}
}
