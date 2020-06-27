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
using DV.ServicePenalty;

namespace PersistentJobsMod
{
	static class Main
	{
		private static UnityModManager.ModEntry thisModEntry;
		private static bool isModBroken = false;
		private static float initialDistanceRegular = 0f;
		private static float initialDistanceAnyJobTaken = 0f;
#if DEBUG
		private static float PERIOD = 60f;
#else
		private static float PERIOD = 5f * 60f;
#endif
		public static float DVJobDestroyDistanceRegular { get { return initialDistanceRegular; } }

		static void Load(UnityModManager.ModEntry modEntry)
		{
			var harmony = HarmonyInstance.Create(modEntry.Info.Id);
			harmony.PatchAll(Assembly.GetExecutingAssembly());
			modEntry.OnToggle = OnToggle;
			thisModEntry = modEntry;
		}

		static bool OnToggle(UnityModManager.ModEntry modEntry, bool isTogglingOn)
		{
			if (SingletonBehaviour<UnusedTrainCarDeleter>.Instance == null)
			{
				// delay initialization
				modEntry.OnUpdate = (entry, delta) =>
				{
					if (SingletonBehaviour<UnusedTrainCarDeleter>.Instance != null)
					{
						modEntry.OnUpdate = null;
						ReplaceCoroutine(isTogglingOn);
					}
				};
				return true;
			}
			else
			{
				ReplaceCoroutine(isTogglingOn);
			}

			if (isModBroken)
			{
				return !isTogglingOn;
			}

			return true;
		}

		static void ReplaceCoroutine(bool isTogglingOn)
		{
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
				thisModEntry.Logger.Log("Injected mod coroutine.");
				SingletonBehaviour<UnusedTrainCarDeleter>.Instance
					.StartCoroutine(TrainCarsCreateJobOrDeleteCheck(PERIOD, Mathf.Max(carsCheckPeriod.Value, 1.0f)));
			}
			else
			{
				thisModEntry.Logger.Log("Restored game coroutine.");
				SingletonBehaviour<UnusedTrainCarDeleter>.Instance.StartCoroutine(
					SingletonBehaviour<UnusedTrainCarDeleter>.Instance.TrainCarsDeleteCheck(carsCheckPeriod.Value)
				);
			}
		}

		static void OnCriticalFailure()
		{
			isModBroken = true;
			thisModEntry.Active = false;
			thisModEntry.Logger.Critical("Deactivating mod PersistentJobs due to critical failure!");
			thisModEntry.Logger.Warning("You can reactivate PersistentJobs by restarting the game, but this failure " +
				"type likely indicates an incompatibility between the mod and a recent game update. Please search the " +
				"mod's Github issue tracker for a relevant report. If none is found, please open one. Include the " +
				"exception message printed above and your game's current build number.");
		}

		// prevents jobs from expiring due to the player's distance from the station
		[HarmonyPatch(typeof(StationController), "ExpireAllAvailableJobsInStation")]
		class StationController_ExpireAllAvailableJobsInStation_Patch
		{
			static bool Prefix()
			{
				// skips the original method entirely when this mod is active
				return !thisModEntry.Active;
			}
		}

		// expands the distance at which the job generation trigger is rearmed
		[HarmonyPatch(typeof(StationJobGenerationRange))]
		[HarmonyPatchAll]
		class StationJobGenerationRange_AllMethods_Patch
		{
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
						e.ToString()
					));
					OnCriticalFailure();
				}
			}
		}

		// expires a job if none of its cars are in range of the starting station on job start attempt
		[HarmonyPatch(typeof(JobValidator), "ProcessJobOverview")]
		class JobValidator_ProcessJobOverview_Patch
		{
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
					if (!job.tasks.Any(CheckTaskForCarsInRange(stationRange)))
					{
						job.ExpireJob();
					}
				}
				catch (Exception e)
				{
					thisModEntry.Logger.Error(string.Format(
						"Exception thrown during JobValidator.ProcessJobOverview prefix patch:\n{0}",
						e.ToString()
					));
					OnCriticalFailure();
				}
			}

			private static Func<Task, bool> CheckTaskForCarsInRange(StationJobGenerationRange stationRange)
			{
				return (Task t) =>
				{
					if (t is ParallelTasks || t is SequentialTasks)
					{
						return Traverse.Create(t)
							.Field("tasks")
							.GetValue<IEnumerable<Task>>()
							.Any(CheckTaskForCarsInRange(stationRange));
					}
					List<Car> cars = Traverse.Create(t).Field("cars").GetValue<List<Car>>();
					Car carInRangeOfStation = cars.FirstOrDefault((Car c) =>
					{
						TrainCar trainCar = TrainCar.GetTrainCarByCarGuid(c.carGuid);
						float distance =
							(trainCar.transform.position - stationRange.stationCenterAnchor.position).sqrMagnitude;
						return trainCar != null && distance <= initialDistanceRegular;
					});
					return carInRangeOfStation != null;
				};
			}
		}

		// generates shunting unload jobs
		[HarmonyPatch(typeof(StationProceduralJobGenerator), "GenerateInChainJob")]
		class StationProceduralJobGenerator_GenerateInChainJob_Patch
		{
			static bool Prefix(
				ref JobChainController __result,
				StationController ___stationController,
				JobType startingJobType,
				bool forceFulfilledLicenseRequirements = false)
			{
				if (thisModEntry.Active)
				{
					try
					{
						if (startingJobType == JobType.ShuntingUnload)
						{
							Debug.Log("[PersistentJobs] gen in shunting unload");
							__result = ShuntingUnloadJobProceduralGenerator.GenerateShuntingUnloadJobWithCarSpawning(
								___stationController,
								forceFulfilledLicenseRequirements,
								new System.Random(Environment.TickCount));
							if (__result != null)
							{
								Debug.Log("[PersistentJobs] finalize in shunting unload");
								__result.FinalizeSetupAndGenerateFirstJob();
							}
							return false;
						}
						Debug.LogWarning(string.Format(
							"[PersistentJobs] Got unexpected JobType.{0} in {1}.{2} {3} patch. Falling back to base method.",
							startingJobType.ToString(),
							"StationProceduralJobGenerator",
							"GenerateInChainJob",
							"prefix"
						));
					}
					catch (Exception e)
					{
						thisModEntry.Logger.Error(string.Format(
							"Exception thrown during {0}.{1} {2} patch:\n{3}",
							"StationProceduralJobGenerator",
							"GenerateInChainJob",
							"prefix",
							e.ToString()
						));
						OnCriticalFailure();
					}
				}
				return true;
			}
		}

		// generates shunting load jobs & freight haul jobs
		[HarmonyPatch(typeof(StationProceduralJobGenerator), "GenerateOutChainJob")]
		class StationProceduralJobGenerator_GenerateOutChainJob_Patch
		{
			static bool Prefix(
				ref JobChainController __result,
				StationController ___stationController,
				JobType startingJobType,
				bool forceFulfilledLicenseRequirements = false)
			{
				if (thisModEntry.Active)
				{
					try
					{
						if (startingJobType == JobType.ShuntingLoad)
						{
							Debug.Log("[PersistentJobs] gen out shunting load");
							__result = ShuntingLoadJobProceduralGenerator.GenerateShuntingLoadJobWithCarSpawning(
								___stationController,
								forceFulfilledLicenseRequirements,
								new System.Random(Environment.TickCount));
							if (__result != null)
							{
								Debug.Log("[PersistentJobs] finalize out shunting load");
								__result.FinalizeSetupAndGenerateFirstJob();
							}
							return false;
						}
						else if (startingJobType == JobType.Transport)
						{
							Debug.Log("[PersistentJobs] gen out transport");
							__result = TransportJobProceduralGenerator.GenerateTransportJobWithCarSpawning(
								___stationController,
								forceFulfilledLicenseRequirements,
								new System.Random(Environment.TickCount));
							if (__result != null)
							{
								Debug.Log("[PersistentJobs] finalize out transport");
								__result.FinalizeSetupAndGenerateFirstJob();
							}
							return false;
						}
						Debug.LogWarning(string.Format(
							"[PersistentJobs] Got unexpected JobType.{0} in {1}.{2} {3} patch. Falling back to base method.",
							startingJobType.ToString(),
							"StationProceduralJobGenerator",
							"GenerateOutChainJob",
							"prefix"
						));
					}
					catch (Exception e)
					{
						thisModEntry.Logger.Error(string.Format(
							"Exception thrown during {0}.{1} {2} patch:\n{3}",
							"StationProceduralJobGenerator",
							"GenerateOutChainJob",
							"prefix",
							e.ToString()
						));
						OnCriticalFailure();
					}
				}
				return true;
			}
		}

		// unload: divert cars that can be loaded at the current station for later generation of ShuntingLoad jobs
		// load: generates a corresponding transport job
		// transport: generates a corresponding unload job
		[HarmonyPatch(typeof(JobChainControllerWithEmptyHaulGeneration), "OnLastJobInChainCompleted")]
		class JobChainControllerWithEmptyHaulGeneration_OnLastJobInChainCompleted_Patch
		{
			static void Prefix(
				JobChainControllerWithEmptyHaulGeneration __instance,
				List<StaticJobDefinition> ___jobChain,
				Job lastJobInChain)
			{
				Debug.Log("[PersistentJobs] last job chain empty haul gen");
				try
				{
					StaticJobDefinition lastJobDef = ___jobChain[___jobChain.Count - 1];
					if (lastJobDef.job != lastJobInChain)
					{
						Debug.LogError(string.Format(
							"[PersistentJobs] lastJobInChain ({0}) does not match lastJobDef.job ({1})",
							lastJobInChain.ID,
							lastJobDef.job.ID));
					}
					else if (lastJobInChain.jobType == JobType.ShuntingUnload)
					{
						Debug.Log("[PersistentJobs] checking static definition type");
						StaticShuntingUnloadJobDefinition unloadJobDef = lastJobDef as StaticShuntingUnloadJobDefinition;
						if (unloadJobDef != null)
						{
							StationController station = SingletonBehaviour<LogicController>.Instance
								.YardIdToStationController[lastJobInChain.chainData.chainDestinationYardId];
							List<CargoGroup> availableCargoGroups = station.proceduralJobsRuleset.outputCargoGroups;

							Debug.Log("[PersistentJobs] diverting trainCars");
							int countCarsDiverted = 0;
							// if a trainCar set can be reused at the current station, keep them there
							for (int i = unloadJobDef.carsPerDestinationTrack.Count - 1; i >= 0; i--)
							{
								CarsPerTrack cpt = unloadJobDef.carsPerDestinationTrack[i];
								// check if there is any cargoGroup that satisfies all the cars
								if (availableCargoGroups.Any(
									cg => cpt.cars.All(
										c => Utilities.GetCargoTypesForCarType(c.carType)
											.Intersect(cg.cargoTypes)
											.Any())))
								{
									// registering the cars as jobless & removing them from carsPerDestinationTrack
									// prevents base method from generating an EmptyHaul job for them
									// they will be candidates for new jobs once the player leaves the area
									List<TrainCar> tcsToDivert = new List<TrainCar>();
									foreach(Car c in cpt.cars)
									{
										tcsToDivert.Add(TrainCar.logicCarToTrainCar[c]);
										tcsToDivert[tcsToDivert.Count - 1].UpdateJobIdOnCarPlates(string.Empty);
									}
									JobDebtController.RegisterJoblessCars(tcsToDivert);
									countCarsDiverted += tcsToDivert.Count;
									unloadJobDef.carsPerDestinationTrack.Remove(cpt);
								}
							}
							Debug.Log(string.Format("[PersistentJobs] diverted {0} trainCars", countCarsDiverted));
						}
						else
						{
							Debug.LogError("[PersistentJobs] Couldn't convert lastJobDef to " +
								"StaticShuntingUnloadJobDefinition. EmptyHaul jobs won't be generated.");
						}
					}
					else if (lastJobInChain.jobType == JobType.ShuntingLoad)
					{
						StaticShuntingLoadJobDefinition loadJobDef = lastJobDef as StaticShuntingLoadJobDefinition;
						if (loadJobDef != null)
						{
							StationController startingStation = SingletonBehaviour<LogicController>.Instance
								.YardIdToStationController[loadJobDef.logicStation.ID];
							StationController destStation = SingletonBehaviour<LogicController>.Instance
								.YardIdToStationController[loadJobDef.chainData.chainDestinationYardId];
							Track startingTrack = loadJobDef.destinationTrack;
							List<TrainCar> trainCars = new List<TrainCar>(__instance.trainCarsForJobChain);
							System.Random rng = new System.Random(Environment.TickCount);
							JobChainController jobChainController
								= TransportJobProceduralGenerator.GenerateTransportJobWithExistingCars(
									startingStation,
									startingTrack,
									destStation,
									trainCars,
									trainCars.Select<TrainCar, CargoType>(tc => tc.logicCar.CurrentCargoTypeInCar)
										.ToList(),
									trainCars.Select<TrainCar, float>(tc => tc.logicCar.LoadedCargoAmount).ToList(),
									rng
								);
							if (jobChainController != null)
							{
								foreach (TrainCar tc in jobChainController.trainCarsForJobChain)
								{
									__instance.trainCarsForJobChain.Remove(tc);
								}
								jobChainController.FinalizeSetupAndGenerateFirstJob();
								Debug.Log(string.Format(
									"[PersistentJobs] Generated job chain [{0}]: {1}",
									jobChainController.jobChainGO.name,
									jobChainController.jobChainGO));
							}
						}
						else
						{
							Debug.LogError(
								"[PersistentJobs] Couldn't convert lastJobDef to StaticShuntingLoadDefinition." +
								" Transport jobs won't be generated."
							);
						}
					}
					else if (lastJobInChain.jobType == JobType.Transport)
					{
						StaticTransportJobDefinition loadJobDef = lastJobDef as StaticTransportJobDefinition;
						if (loadJobDef != null)
						{
							StationController startingStation = SingletonBehaviour<LogicController>.Instance
								.YardIdToStationController[loadJobDef.logicStation.ID];
							StationController destStation = SingletonBehaviour<LogicController>.Instance
								.YardIdToStationController[loadJobDef.chainData.chainDestinationYardId];
							Track startingTrack = loadJobDef.destinationTrack;
							List<TrainCar> trainCars = new List<TrainCar>(__instance.trainCarsForJobChain);
							System.Random rng = new System.Random(Environment.TickCount);
							JobChainController jobChainController
								= ShuntingUnloadJobProceduralGenerator.GenerateShuntingUnloadJobWithExistingCars(
									startingStation,
									startingTrack,
									destStation,
									trainCars,
									trainCars.Select<TrainCar, CargoType>(tc => tc.logicCar.CurrentCargoTypeInCar)
										.ToList(),
									rng
								);
							if (jobChainController != null)
							{
								foreach (TrainCar tc in jobChainController.trainCarsForJobChain)
								{
									__instance.trainCarsForJobChain.Remove(tc);
								}
								jobChainController.FinalizeSetupAndGenerateFirstJob();
								Debug.Log(string.Format(
									"[PersistentJobs] Generated job chain [{0}]: {1}",
									jobChainController.jobChainGO.name,
									jobChainController.jobChainGO));
							}
						}
						else
						{
							Debug.LogError(
								"[PersistentJobs] Couldn't convert lastJobDef to StaticTransportDefinition." +
								" ShuntingUnload jobs won't be generated."
							);
						}
					}
					else
					{
						Debug.LogError(string.Format(
							"[PersistentJobs] Unexpected job type: {0}. The last job in chain must be " +
							"ShuntingLoad, Transport, or ShuntingUnload for JobChainControllerWithEmptyHaulGeneration patch! " +
							"New jobs won't be generated.",
							lastJobInChain.jobType));
					}
				}
				catch (Exception e)
				{
					thisModEntry.Logger.Error(string.Format(
							"Exception thrown during {0}.{1} {2} patch:\n{3}",
							"JobChainControllerWithEmptyHaulGeneration",
							"OnLastJobInChainCompleted",
							"prefix",
							e.ToString()));
					OnCriticalFailure();
				}
			}
		}

		// tries to generate new shunting load jobs for the train cars marked for deletion
		// failing that, the train cars are deleted
		[HarmonyPatch(typeof(UnusedTrainCarDeleter), "InstantConditionalDeleteOfUnusedCars")]
		class UnusedTrainCarDeleter_InstantConditionalDeleteOfUnusedCars_Patch
		{
			static bool Prefix(
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

						Debug.Log("[PersistentJobs] collecting deletion candidates");
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
								.Method("AreDeleteConditionsFulfilled", new Type[] { typeof(TrainCar) })
								.GetValue<bool>(trainCar);
							if (areDeleteConditionsFulfilled)
							{
								___unusedTrainCarsMarkedForDelete.RemoveAt(i);
								trainCarsToDelete.Add(trainCar);
								___carVisitCheckersMap.Remove(trainCar);
							}
						}
						Debug.Log(string.Format(
							"[PersistentJobs] found {0} cars marked for deletion",
							trainCarsToDelete.Count));
						if (trainCarsToDelete.Count == 0)
						{
							return false;
						}

						// ------ BEGIN JOB GENERATION ------
						// group trainCars by trainset
						Debug.Log("[PersistentJobs] grouping trainCars by trainSet");
						Dictionary<Trainset, List<TrainCar>> trainCarsPerTrainSet
								= ShuntingLoadJobProceduralGenerator.GroupTrainCarsByTrainset(trainCarsToDelete);
						Debug.Log(string.Format(
							"[PersistentJobs] found {0} trainSets",
							trainCarsPerTrainSet.Count));

						// group trainCars sets by nearest stationController
						Debug.Log("[PersistentJobs] grouping trainSets by nearest station");
						Dictionary<StationController, List<(List<TrainCar>, List<CargoGroup>)>> cgsPerTcsPerSc
							= ShuntingLoadJobProceduralGenerator.GroupTrainCarSetsByNearestStation(trainCarsPerTrainSet);
						Debug.Log(string.Format(
							"[PersistentJobs] found {0} stations",
							cgsPerTcsPerSc.Count));

						// populate possible cargoGroups per group of trainCars
						Debug.Log("[PersistentJobs] populating cargoGroups");
						ShuntingLoadJobProceduralGenerator.PopulateCargoGroupsPerTrainCarSet(cgsPerTcsPerSc);

						// pick new jobs for the trainCars at each station
						Debug.Log("[PersistentJobs] picking jobs");
						System.Random rng = new System.Random(Environment.TickCount);
						List<(StationController, List<CarsPerTrack>, StationController, List<TrainCar>, List<CargoType>)>
							jobInfos = ShuntingLoadJobProceduralGenerator
								.ComputeJobInfosFromCargoGroupsPerTrainCarSetPerStation(cgsPerTcsPerSc, rng);
						Debug.Log(string.Format(
							"[PersistentJobs] chose {0} jobs",
							jobInfos.Count));

						// try to generate jobs
						Debug.Log("[PersistentJobs] generating jobs");
						IEnumerable<JobChainController> jobChainControllers
							= ShuntingLoadJobProceduralGenerator.doJobGeneration(jobInfos, rng);
						Debug.Log(string.Format(
							"[PersistentJobs] generated {0} jobs",
							jobChainControllers.Where(jcc => jcc != null).Count()));

						// preserve trainCars for which a new job was generated
						Debug.Log("[PersistentJobs] preserving cars");
						int totalCarsPreserved = 0;
						foreach (JobChainController jcc in jobChainControllers)
						{
							if (jcc != null)
							{
								jcc.trainCarsForJobChain.ForEach(tc => trainCarsToDelete.Remove(tc));
								totalCarsPreserved += jcc.trainCarsForJobChain.Count;
								jcc.FinalizeSetupAndGenerateFirstJob();
							}
						}
						Debug.Log(string.Format("[PersistentJobs] preserved {0} cars", totalCarsPreserved));
						// ------ END JOB GENERATION ------

						SingletonBehaviour<CarSpawner>.Instance.DeleteTrainCars(trainCarsToDelete, true);
						Debug.Log(string.Format("[PersistentJobs] deleted {0} cars", trainCarsToDelete.Count));
						return false;
					}
					catch (Exception e)
					{
						thisModEntry.Logger.Error(string.Format(
							"Exception thrown during {0}.{1} {2} patch:\n{3}",
							"UnusedTrainCarDeleter",
							"InstantConditionalDeleteOfUnusedCars",
							"prefix",
							e.ToString()
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
		public static IEnumerator TrainCarsCreateJobOrDeleteCheck(float period, float interopPeriod)
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
				AreDeleteConditionsFulfilledMethod
					= unusedTrainCarDeleterTraverser.Method("AreDeleteConditionsFulfilled", new Type[] { typeof(TrainCar) });
			}
			catch (Exception e)
			{
				thisModEntry.Logger.Error(string.Format(
					"Exception thrown during TrainCarsCreateJobOrDeleteCheck setup:\n{0}",
					e.ToString()
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
						e.ToString()
					));
					OnCriticalFailure();
				}

				Debug.Log("[PersistentJobs] collecting deletion candidates (coroutine)");
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
					Debug.Log(string.Format(
						"[PersistentJobs] found {0} cars marked for deletion (coroutine)",
						trainCarCandidatesForDelete.Count));
					if (trainCarCandidatesForDelete.Count == 0)
					{
						continue;
					}
				}
				catch (Exception e)
				{
					thisModEntry.Logger.Error(string.Format(
						"Exception thrown during TrainCarsCreateJobOrDeleteCheck delete candidate collection:\n{0}",
						e.ToString()
					));
					OnCriticalFailure();
				}

				yield return WaitFor.SecondsRealtime(interopPeriod);

				// ------ BEGIN JOB GENERATION ------
				// group trainCars by trainset
				Debug.Log("[PersistentJobs] grouping trainCars by trainSet (coroutine)");
				Dictionary<Trainset, List<TrainCar>> trainCarsPerTrainSet = null;
				try
				{
					trainCarsPerTrainSet
						= ShuntingLoadJobProceduralGenerator.GroupTrainCarsByTrainset(trainCarCandidatesForDelete);
				}
				catch (Exception e)
				{
					thisModEntry.Logger.Error(string.Format(
						"Exception thrown during TrainCarsCreateJobOrDeleteCheck trainset grouping:\n{0}",
						e.ToString()
					));
					OnCriticalFailure();
				}
				Debug.Log(string.Format(
					"[PersistentJobs] found {0} trainSets (coroutine)",
					trainCarsPerTrainSet.Count));

				yield return WaitFor.SecondsRealtime(interopPeriod);

				// group trainCars sets by nearest stationController
				Debug.Log("[PersistentJobs] grouping trainSets by nearest station (coroutine)");
				Dictionary<StationController, List<(List<TrainCar>, List<CargoGroup>)>> cgsPerTcsPerSc = null;
				try
				{
					cgsPerTcsPerSc
						= ShuntingLoadJobProceduralGenerator.GroupTrainCarSetsByNearestStation(trainCarsPerTrainSet);
				}
				catch (Exception e)
				{
					thisModEntry.Logger.Error(string.Format(
						"Exception thrown during TrainCarsCreateJobOrDeleteCheck station grouping:\n{0}",
						e.ToString()
					));
					OnCriticalFailure();
				}
				Debug.Log(string.Format(
					"[PersistentJobs] found {0} stations (coroutine)",
					cgsPerTcsPerSc.Count));

				yield return WaitFor.SecondsRealtime(interopPeriod);

				// populate possible cargoGroups per group of trainCars
				Debug.Log("[PersistentJobs] populating cargoGroups (coroutine)");
				try
				{
					ShuntingLoadJobProceduralGenerator.PopulateCargoGroupsPerTrainCarSet(cgsPerTcsPerSc);
				}
				catch (Exception e)
				{
					thisModEntry.Logger.Error(string.Format(
						"Exception thrown during TrainCarsCreateJobOrDeleteCheck cargoGroup population:\n{0}",
						e.ToString()
					));
					OnCriticalFailure();
				}

				yield return WaitFor.SecondsRealtime(interopPeriod);

				// pick new jobs for the trainCars at each station
				Debug.Log("[PersistentJobs] picking jobs (coroutine)");
				System.Random rng = new System.Random(Environment.TickCount);
				List<(StationController, List<CarsPerTrack>, StationController, List<TrainCar>, List<CargoType>)>
					jobInfos = null;
				try
				{
					jobInfos = ShuntingLoadJobProceduralGenerator
						.ComputeJobInfosFromCargoGroupsPerTrainCarSetPerStation(cgsPerTcsPerSc, rng);
				}
				catch (Exception e)
				{
					thisModEntry.Logger.Error(string.Format(
						"Exception thrown during TrainCarsCreateJobOrDeleteCheck job info selection:\n{0}",
						e.ToString()
					));
					OnCriticalFailure();
				}
				Debug.Log(string.Format(
					"[PersistentJobs] chose {0} jobs (coroutine)",
					jobInfos.Count));

				yield return WaitFor.SecondsRealtime(interopPeriod);

				// try to generate jobs
				Debug.Log("[PersistentJobs] generating jobs (coroutine)");
				IEnumerable<JobChainController> jobChainControllers = null;
				try
				{
					jobChainControllers
						= ShuntingLoadJobProceduralGenerator.doJobGeneration(jobInfos, rng);
				}
				catch (Exception e)
				{
					thisModEntry.Logger.Error(string.Format(
						"Exception thrown during TrainCarsCreateJobOrDeleteCheck job generation:\n{0}",
						e.ToString()
					));
					OnCriticalFailure();
				}
				Debug.Log(string.Format(
					"[PersistentJobs] generated {0} jobs (coroutine)",
					jobChainControllers.Where(jcc => jcc != null).Count()));

				yield return WaitFor.SecondsRealtime(interopPeriod);

				// preserve trainCars for which a new job was generated
				Debug.Log("[PersistentJobs] preserving cars (coroutine)");
				int totalCarsPreserved = 0;
				try
				{
					foreach (JobChainController jcc in jobChainControllers)
					{
						if (jcc != null)
						{
							jcc.trainCarsForJobChain.ForEach(tc => trainCarCandidatesForDelete.Remove(tc));
							totalCarsPreserved += jcc.trainCarsForJobChain.Count;
							jcc.FinalizeSetupAndGenerateFirstJob();
						}
					}
					Debug.Log(string.Format("[PersistentJobs] preserved {0} cars (coroutine)", totalCarsPreserved));
				}
				catch (Exception e)
				{
					thisModEntry.Logger.Error(string.Format(
						"Exception thrown during TrainCarsCreateJobOrDeleteCheck trainCar preservation:\n{0}",
						e.ToString()
					));
					OnCriticalFailure();
				}
				// ------ END JOB GENERATION ------

				yield return WaitFor.SecondsRealtime(interopPeriod);

				Debug.Log("[PersistentJobs] deleting cars (coroutine)");
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
					Debug.Log(string.Format("[PersistentJobs] deleted {0} cars (coroutine)", trainCarsToDelete.Count));
				}
				catch (Exception e)
				{
					thisModEntry.Logger.Error(string.Format(
						"Exception thrown during TrainCarsCreateJobOrDeleteCheck car deletion:\n{0}",
						e.ToString()
					));
					OnCriticalFailure();
				}
			}
		}

		// chooses the shortest track with enough space (instead of the first track found)
		[HarmonyPatch(typeof(YardTracksOrganizer), "GetTrackThatHasEnoughFreeSpace")]
		class YardTracksOrganizer_GetTrackThatHasEnoughFreeSpace_Patch
		{
			static bool Prefix(YardTracksOrganizer __instance, ref Track __result, List<Track> tracks, float requiredLength)
			{
				if (thisModEntry.Active)
				{
					Debug.Log("[PersistentJobs] getting track with free space");
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
							__result = tracksSortedByLength.First().Value;
						}
						return false;
					}
					catch (Exception e)
					{
						Debug.LogWarning(string.Format(
							"[PersistentJobs] Exception thrown during {0}.{1} {2} patch:\n{3}\nFalling back on base method.",
							"YardTracksOrganizer",
							"GetTrackThatHasEnoughFreeSpace",
							"prefix",
							e.ToString()
						));
					}
				}
				return true;
			}
		}
	}
}