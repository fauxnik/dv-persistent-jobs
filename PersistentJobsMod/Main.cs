using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Harmony12;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityModManagerNet;
using DV.Logic.Job;
using DV.ServicePenalty;

namespace PersistentJobsMod
{
	static class Main
	{
		public static UnityModManager.ModEntry modEntry;
		private static bool isModBroken = false;
		private static bool overrideTrackReservation = false;
		private static float initialDistanceRegular = 0f;
		private static float initialDistanceAnyJobTaken = 0f;
		private static List<string> stationIdSpawnBlockList = new List<string>();

		private static readonly string SAVE_DATA_PRIMARY_KEY = "Mod_Persistent_Jobs";
		private static readonly string SAVE_DATA_VERSION_KEY = "version";
		private static readonly string SAVE_DATA_SPAWN_BLOCK_KEY = "spawn_block";

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
			Main.modEntry = modEntry;
		}

		static bool OnToggle(UnityModManager.ModEntry modEntry, bool isTogglingOn)
		{
			bool isTogglingOff = !isTogglingOn;

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

			if (isTogglingOff)
			{
				stationIdSpawnBlockList.Clear();
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
				modEntry.Logger.Log("Injected mod coroutine.");
				SingletonBehaviour<UnusedTrainCarDeleter>.Instance
					.StartCoroutine(UnusedTrainCarDeleterPatch.TrainCarsCreateJobOrDeleteCheck(PERIOD, Mathf.Max(carsCheckPeriod.Value, 1.0f)));
			}
			else
			{
				modEntry.Logger.Log("Restored game coroutine.");
				SingletonBehaviour<UnusedTrainCarDeleter>.Instance.StartCoroutine(
					SingletonBehaviour<UnusedTrainCarDeleter>.Instance.TrainCarsDeleteCheck(carsCheckPeriod.Value)
				);
			}
		}

		public static void OnCriticalFailure()
		{
			isModBroken = true;
			modEntry.Active = false;
			modEntry.Logger.Critical("Deactivating mod PersistentJobs due to critical failure!");
			modEntry.Logger.Warning("You can reactivate PersistentJobs by restarting the game, but this failure " +
				"type likely indicates an incompatibility between the mod and a recent game update. Please search the " +
				"mod's Github issue tracker for a relevant report. If none is found, please open one. Include the " +
				"exception message printed above and your game's current build number.");
		}

		[HarmonyPatch(typeof(SaveGameManager), "Save")]
		class SaveGameManager_Save_Patch
		{
			static void Prefix(SaveGameManager __instance)
			{
				try
				{
					JArray spawnBlockSaveData = new JArray(from id in stationIdSpawnBlockList select new JValue(id));

					JObject saveData = new JObject(
						new JProperty(SAVE_DATA_VERSION_KEY, new JValue(modEntry.Version.ToString())),
						new JProperty(SAVE_DATA_SPAWN_BLOCK_KEY, spawnBlockSaveData));

					SaveGameManager.data.SetJObject(SAVE_DATA_PRIMARY_KEY, saveData);
				}
				catch (Exception e)
				{
					// TODO: what to do if saving fails?
					modEntry.Logger.Warning(string.Format("Saving mod data failed with exception:\n{0}", e));
				}
			}
		}

		[HarmonyPatch(typeof(SaveGameManager), "Load")]
		class SaveGameManager_Load_Patch
		{
			static void Postfix(SaveGameManager __instance)
			{
				try
				{
					JObject saveData = SaveGameManager.data.GetJObject(SAVE_DATA_PRIMARY_KEY);

					if (saveData == null)
					{
						modEntry.Logger.Log("Not loading save data: primary object was null.");
						return;
					}

					JArray spawnBlockSaveData = (JArray)saveData[SAVE_DATA_SPAWN_BLOCK_KEY];

					if (spawnBlockSaveData == null)
					{
						modEntry.Logger.Log("Not loading spawn block list: data was null.");
						return;
					}

					stationIdSpawnBlockList = spawnBlockSaveData.Select(id => (string)id).ToList();
					modEntry.Logger.Log(
						string.Format("Loaded station spawn block list: [ {0} ]",
						string.Join(", ", stationIdSpawnBlockList)));
				}
				catch (Exception e)
				{
					// TODO: what to do if loading fails?
					modEntry.Logger.Warning(string.Format("Loading mod data failed with exception:\n{0}", e));
				}
			}
		}

		// reserves tracks for taken jobs when loading save file
		[HarmonyPatch(typeof(JobSaveManager), "LoadJobChain")]
		class JobSaveManager_LoadJobChain_Patch
		{
			static void Postfix(JobChainSaveData chainSaveData)
			{
				try
				{
					if (chainSaveData.jobTaken)
					{
						// reserve space for this job
						StationProceduralJobsController[] stationJobControllers
							= UnityEngine.Object.FindObjectsOfType<StationProceduralJobsController>();
						JobChainController jobChainController = null;
						for (int i = 0; i < stationJobControllers.Length && jobChainController == null; i++)
						{
							foreach (JobChainController jcc in stationJobControllers[i].GetCurrentJobChains())
							{
								if (jcc.currentJobInChain.ID == chainSaveData.firstJobId)
								{
									jobChainController = jcc;
									break;
								}
							}
						}
						if (jobChainController == null)
						{
							Debug.LogWarning(string.Format(
								"[PersistentJobs] could not find JobChainController for Job[{0}]; skipping track reservation!",
								chainSaveData.firstJobId));
						}
						else if (jobChainController.currentJobInChain.jobType == JobType.ShuntingLoad)
						{
							Debug.Log(string.Format(
							"[PersistentJobs] skipping track reservation for Job[{0}] because it's a shunting load job",
							jobChainController.currentJobInChain.ID));
						}
						else
						{
							overrideTrackReservation = true;
							Traverse.Create(jobChainController).Method("ReserveRequiredTracks", new Type[] { }).GetValue();
							overrideTrackReservation = false;
						}
					}
				}
				catch (Exception e)
				{
					// TODO: what to do if reserving tracks fails?
					modEntry.Logger.Warning(string.Format("Reserving track space for Job[{1}] failed with exception:\n{0}", e, chainSaveData.firstJobId));
				}
			}
		}

		// prevents jobs from expiring due to the player's distance from the station
		[HarmonyPatch(typeof(StationController), "ExpireAllAvailableJobsInStation")]
		class StationController_ExpireAllAvailableJobsInStation_Patch
		{
			static bool Prefix()
			{
				// skips the original method entirely when this mod is active
				return !modEntry.Active;
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

					if (modEntry.Active)
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
					modEntry.Logger.Error(string.Format(
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
			static bool Prefix(
				List<StationController> ___allStations,
				DV.Printers.PrinterController ___bookletPrinter,
				JobOverview jobOverview)
			{
				try
				{
					if (!modEntry.Active)
					{
						return true;
					}

					Job job = jobOverview.job;
					StationController stationController = ___allStations.FirstOrDefault(
						(StationController st) => st.logicStation.availableJobs.Contains(job)
					);

					if (___bookletPrinter.IsOnCooldown || job.State != JobState.Available || stationController == null)
					{
						return true;
					}

					// for shunting (un)load jobs, require cars to not already be on the warehouse track
					if (job.jobType == JobType.ShuntingLoad || job.jobType == JobType.ShuntingUnload)
					{
						WarehouseTask wt = job.tasks.Aggregate(
							null as Task,
							(found, outerTask) => found == null
								? Utilities.TaskFindDFS(outerTask, innerTask => innerTask is WarehouseTask)
								: found) as WarehouseTask;
						WarehouseMachine wm = wt != null ? wt.warehouseMachine : null;
						if (wm != null && job.tasks.Any(
							outerTask => Utilities.TaskAnyDFS(
								outerTask,
								innerTask => IsAnyTaskCarOnTrack(innerTask, wm.WarehouseTrack))))
						{
							___bookletPrinter.PlayErrorSound();
							return false;
						}
					}

					// expire the job if all associated cars are outside the job destruction range
					// the base method's logic will handle generating the expired report
					StationJobGenerationRange stationRange = Traverse.Create(stationController)
						.Field("stationRange")
						.GetValue<StationJobGenerationRange>();
					if (!job.tasks.Any(
						outerTask => Utilities.TaskAnyDFS(
							outerTask,
							innerTask => AreTaskCarsInRange(innerTask, stationRange))))
					{
						job.ExpireJob();
						return true;
					}

					// reserve space for this job
					StationProceduralJobsController[] stationJobControllers
						= UnityEngine.Object.FindObjectsOfType<StationProceduralJobsController>();
					JobChainController jobChainController = null;
					for (int i = 0; i < stationJobControllers.Length && jobChainController == null; i++)
					{
						foreach (JobChainController jcc in stationJobControllers[i].GetCurrentJobChains())
						{
							if (jcc.currentJobInChain == job)
							{
								jobChainController = jcc;
								break;
							}
						}
					}
					if (jobChainController == null)
					{
						Debug.LogWarning(string.Format(
							"[PersistentJobs] could not find JobChainController for Job[{0}]",
							job.ID));
					}
					else if (job.jobType == JobType.ShuntingLoad)
					{
						// shunting load jobs don't need to reserve space
						// their destination track task will be changed to the warehouse track
						Debug.Log(string.Format(
							"[PersistentJobs] skipping track reservation for Job[{0}] because it's a shunting load job",
							job.ID));
					}
					else
					{
						ReserveOrReplaceRequiredTracks(jobChainController);
					}

					// for shunting load jobs, don't require player to spot the train on a track after loading
					if (job.jobType == JobType.ShuntingLoad)
					{
						ReplaceShuntingLoadDestination(job);
					}
				}
				catch (Exception e)
				{
					modEntry.Logger.Error(string.Format(
						"Exception thrown during JobValidator.ProcessJobOverview prefix patch:\n{0}",
						e.ToString()
					));
					OnCriticalFailure();
				}
				return true;
			}

			private static void ReplaceShuntingLoadDestination(Job job)
			{
				Debug.Log("[PersistentJobs] attempting to replace destination track with warehouse track...");
				SequentialTasks sequence = job.tasks[0] as SequentialTasks;
				if (sequence == null)
				{
					Debug.LogError("    couldn't find sequential task!");
					return;
				}

				LinkedList<Task> tasks = Traverse.Create(sequence)
					.Field("tasks")
					.GetValue<LinkedList<Task>>();

				if (tasks == null)
				{
					Debug.LogError("    couldn't find child tasks!");
					return;
				}

				LinkedListNode<Task> cursor = tasks.First;

				if (cursor == null)
				{
					Debug.LogError("    first task in sequence was null!");
					return;
				}

				while (cursor != null && Utilities.TaskAnyDFS(
					cursor.Value,
					t => t.InstanceTaskType != TaskType.Warehouse))
				{
					Debug.Log("    searching for warehouse task...");
					cursor = cursor.Next;
				}

				if (cursor == null)
				{
					Debug.LogError("    couldn't find warehouse task!");
					return;
				}

				// cursor points at the parallel task of warehouse tasks
				// replace the destination track of all following tasks with the warehouse track
				WarehouseTask wt = (Utilities.TaskFindDFS(
					cursor.Value,
					t => t.InstanceTaskType == TaskType.Warehouse) as WarehouseTask);
				WarehouseMachine wm = wt != null ? wt.warehouseMachine : null;

				if (wm == null)
				{
					Debug.LogError("    couldn't find warehouse machine!");
					return;
				}

				while ((cursor = cursor.Next) != null)
				{
					Debug.Log("    replace destination tracks...");
					Utilities.TaskDoDFS(
						cursor.Value,
						t => Traverse.Create(t).Field("destinationTrack").SetValue(wm.WarehouseTrack));
				}

				Debug.Log("    done!");
			}

			private static bool AreTaskCarsInRange(Task task, StationJobGenerationRange stationRange)
			{
				List<Car> cars = Traverse.Create(task).Field("cars").GetValue<List<Car>>();
				Car carInRangeOfStation = cars.FirstOrDefault((Car c) =>
				{
					TrainCar trainCar = TrainCar.GetTrainCarByCarGuid(c.carGuid);
					float distance =
						(trainCar.transform.position - stationRange.stationCenterAnchor.position).sqrMagnitude;
					return trainCar != null && distance <= initialDistanceRegular;
				});
				return carInRangeOfStation != null;
			}

			private static bool IsAnyTaskCarOnTrack(Task task, Track track)
			{
				List<Car> cars = Traverse.Create(task).Field("cars").GetValue<List<Car>>();
				return cars.Any(car => car.CurrentTrack == track);
			}

			private static void ReserveOrReplaceRequiredTracks(JobChainController jobChainController)
			{
				List<StaticJobDefinition> jobChain = Traverse.Create(jobChainController)
							.Field("jobChain")
							.GetValue<List<StaticJobDefinition>>();
				Dictionary<StaticJobDefinition, List<TrackReservation>> jobDefToCurrentlyReservedTracks
					= Traverse.Create(jobChainController)
						.Field("jobDefToCurrentlyReservedTracks")
						.GetValue<Dictionary<StaticJobDefinition, List<TrackReservation>>>();
				for (int i = 0; i < jobChain.Count; i++)
				{
					StaticJobDefinition key = jobChain[i];
					if (jobDefToCurrentlyReservedTracks.ContainsKey(key))
					{
						List<TrackReservation> trackReservations = jobDefToCurrentlyReservedTracks[key];
						for (int j = 0; j < trackReservations.Count; j++)
						{
							Track reservedTrack = trackReservations[j].track;
							float reservedLength = trackReservations[j].reservedLength;
							if (YardTracksOrganizer.Instance.GetFreeSpaceOnTrack(reservedTrack) >= reservedLength)
							{
								YardTracksOrganizer.Instance.ReserveSpace(reservedTrack, reservedLength, false);
							}
							else
							{
								// not enough space to reserve; find a different track with enough space & update job data
								Track replacementTrack = GetReplacementTrack(reservedTrack, reservedLength);
								if (replacementTrack == null)
								{
									Debug.LogWarning(string.Format(
										"[PersistentJobs] Can't find track with enough free space for Job[{0}]. Skipping track reservation!",
										key.job.ID));
									continue;
								}

								YardTracksOrganizer.Instance.ReserveSpace(replacementTrack, reservedLength, false);

								// update reservation data
								trackReservations.RemoveAt(j);
								trackReservations.Insert(j, new TrackReservation(replacementTrack, reservedLength));

								// update static job definition data
								if (key is StaticEmptyHaulJobDefinition)
								{
									(key as StaticEmptyHaulJobDefinition).destinationTrack = replacementTrack;
								}
								else if (key is StaticShuntingLoadJobDefinition)
								{
									(key as StaticShuntingLoadJobDefinition).destinationTrack = replacementTrack;
								}
								else if (key is StaticTransportJobDefinition)
								{
									(key as StaticTransportJobDefinition).destinationTrack = replacementTrack;
								}
								else if (key is StaticShuntingUnloadJobDefinition)
								{
									(key as StaticShuntingUnloadJobDefinition).carsPerDestinationTrack
										= (key as StaticShuntingUnloadJobDefinition).carsPerDestinationTrack
											.Select(cpt => cpt.track == reservedTrack ? new CarsPerTrack(replacementTrack, cpt.cars) : cpt)
											.ToList();
								}
								else
								{
									throw new ArgumentOutOfRangeException(string.Format(
										"[PersistentJobs] Unaccounted for JobType[{1}] encountered while reserving track space for Job[{0}].",
										key.job.ID,
										key.job.jobType));
								}

								// update task data
								foreach(Task task in key.job.tasks)
								{
									Utilities.TaskDoDFS(task, t =>
									{
										if (t is TransportTask)
										{
											Traverse destinationTrack = Traverse.Create(t).Field("destinationTrack");
											if (destinationTrack.GetValue<Track>() == reservedTrack)
											{
												destinationTrack.SetValue(replacementTrack);
											}
										}
									});
								}
							}
						}
					}
					else
					{
						Debug.LogError(
							string.Format(
								"[PersistentJobs] No reservation data for {0}[{1}] found!" +
								" Reservation data can be empty, but it needs to be in {2}.",
								"jobChain",
								i,
								"jobDefToCurrentlyReservedTracks"),
							jobChain[i]);
					}
				}
			}

			private static Track GetReplacementTrack(Track oldTrack, float trainLength)
			{
				// find station controller for track
				StationController[] allStations = UnityEngine.Object.FindObjectsOfType<StationController>();
				StationController stationController
					= allStations.ToList().Find(sc => sc.stationInfo.YardID == oldTrack.ID.yardId);

				// setup preferred tracks
				List<Track>[] preferredTracks;
				Yard stationYard = stationController.logicStation.yard;
				if (stationYard.StorageTracks.Contains(oldTrack))
				{
					// shunting unload, logistical haul
					preferredTracks = new List<Track>[] {
						stationYard.StorageTracks,
						stationYard.TransferOutTracks,
						stationYard.TransferInTracks };
				}
				else if (stationYard.TransferInTracks.Contains(oldTrack))
				{
					// freight haul
					preferredTracks = new List<Track>[] {
						stationYard.TransferInTracks,
						stationYard.TransferOutTracks,
						stationYard.StorageTracks };
				}
				else if (stationYard.TransferOutTracks.Contains(oldTrack))
				{
					// shunting load
					preferredTracks = new List<Track>[] {
						stationYard.TransferOutTracks,
						stationYard.StorageTracks,
						stationYard.TransferInTracks };
				}
				else
				{
					Debug.LogError(string.Format(
						"[PersistentJobs] Cant't find track group for Track[{0}] in Station[{1}]. Skipping reservation!",
						oldTrack.ID,
						stationController.logicStation.ID));
					return null;
				}

				// find track with enough free space
				Track targetTrack = null;
				YardTracksOrganizer yto = YardTracksOrganizer.Instance;
				for (int p = 0; targetTrack == null && p < preferredTracks.Length; p++)
				{
					List<Track> trackGroup = preferredTracks[p];
					targetTrack = yto.GetTrackThatHasEnoughFreeSpace(trackGroup, trainLength);
				}

				if (targetTrack == null)
				{
					Debug.LogWarning(string.Format(
						"[PersistentJobs] Cant't find any track to replace Track[{0}] in Station[{1}]. Skipping reservation!",
						oldTrack.ID,
						stationController.logicStation.ID));
				}

				return targetTrack;
			}
		}

		[HarmonyPatch(typeof(JobChainController), "ReserveRequiredTracks")]
		class JobChainController_ReserveRequiredTracks_Patch
		{
			static bool Prefix()
			{
				if (modEntry.Active && !overrideTrackReservation)
				{
					Debug.Log("[PersistentJobs] skipping track reservation");
					return false;
				}
				return true;
			}
		}

		[HarmonyPatch(typeof(StationProceduralJobsController), "TryToGenerateJobs")]
		class StationProceduralJobsController_TryToGenerateJobs_Patch
		{
			static bool Prefix(StationProceduralJobsController __instance)
			{
				if (modEntry.Active)
				{
					return !stationIdSpawnBlockList.Contains(__instance.stationController.logicStation.ID);
				}
				return true;
			}

			static void Postfix(StationProceduralJobsController __instance)
			{
				string stationId = __instance.stationController.logicStation.ID;
				if (!stationIdSpawnBlockList.Contains(stationId))
				{
					stationIdSpawnBlockList.Add(stationId);
				}
			}
		}

		// generates shunting unload jobs
		[HarmonyPatch(typeof(StationProceduralJobGenerator), "GenerateInChainJob")]
		class StationProceduralJobGenerator_GenerateInChainJob_Patch
		{
			static bool Prefix(ref JobChainController __result)
			{
				if (modEntry.Active)
				{
					Debug.Log("[PersistentJobs] cancelling inbound job spawning" +
						" to keep tracks clear for outbound jobs from other stations");
					__result = null;
					return false;
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
				if (modEntry.Active)
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
						modEntry.Logger.Error(string.Format(
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
					modEntry.Logger.Error(string.Format(
							"Exception thrown during {0}.{1} {2} patch:\n{3}",
							"JobChainControllerWithEmptyHaulGeneration",
							"OnLastJobInChainCompleted",
							"prefix",
							e.ToString()));
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
				if (modEntry.Active)
				{
					Debug.Log("[PersistentJobs] getting random track with free space");
					try
					{
						__result = null;
						List<Track> tracksWithFreeSpace = new List<Track>();
						foreach (Track track in tracks)
						{
							double freeSpaceOnTrack = __instance.GetFreeSpaceOnTrack(track);
							if (freeSpaceOnTrack > (double)requiredLength)
							{
								tracksWithFreeSpace.Add(track);
							}
						}
						Debug.Log(string.Format(
							"[PersistentJobs] {0}/{1} tracks have at least {2}m available",
							tracksWithFreeSpace.Count,
							tracks.Count,
							requiredLength));
						if (tracksWithFreeSpace.Count > 0)
						{
							__result = Utilities.GetRandomFromEnumerable(
								tracksWithFreeSpace,
								new System.Random());
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
							e.ToString()));
					}
				}
				return true;
			}
		}
	}
}