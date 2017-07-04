﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Configuration;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;
using RuneOptim;

namespace RuneApp
{
	public enum LoadSaveResult
	{
		Failure = -2,
		FileNotFound = -1,
		EmptyFile = 0,
		Success = 1,
	}

	class progLogger : TextWriter
	{
		public override Encoding Encoding
		{
			get
			{
				return Encoding.Default;
			}
		}

		public override void WriteLine(string value)
		{
			Program.log.Debug(value);
		}
	}

	public static class Program
	{
		public static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
		public static readonly Configuration config = ConfigurationManager.OpenExeConfiguration(Application.ExecutablePath);
		
		public static Save data;

		public static Properties.Settings Settings
		{
			get
			{
				//var qwete = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.PerUserRoamingAndLocal).FilePath;
				//Console.WriteLine(qwete);
				//Properties.Settings.Default.Upgrade();
				return Properties.Settings.Default;
			}
		}

		public static bool WatchSave
		{
			get { return Settings.WatchSave; }
			set
			{
				// TODO: start/stop Directory watcher
				Settings.WatchSave = value;
				Settings.Save();
			}
		}

		private static bool DoesSettingExist(string settingName)
		{
			return Properties.Settings.Default.Properties.Cast<SettingsProperty>().Any(prop => prop.Name == settingName);
		}
		
		public static event EventHandler<PrintToEventArgs> BuildsProgressTo;

		/// <summary>
		/// The list of build definitions
		/// </summary>
		public static readonly ObservableCollection<Build> builds = new ObservableCollection<Build>();

		/// <summary>
		/// The current list of loadouts generated by running builds.
		/// </summary>
		public static readonly ObservableCollection<Loadout> loads = new ObservableCollection<Loadout>();

		private static bool isRunning = false;
		private static Build currentBuild = null;
		private static Task runTask = null;
		private static CancellationToken runToken;
		private static CancellationTokenSource runSource = null;

		public static readonly RuneSheet runeSheet = new RuneSheet();

		public static readonly InternalServer.Master master = new InternalServer.Master();
		public static bool goodRunes;

		static FileSystemWatcher saveFileWatcher = null;
		static System.Timers.Timer saveFileDebouncer = null;
		public static event EventHandler saveFileTouched;

		/// <summary>
		/// The main entry point for the application.
		/// </summary>
		[STAThread]
		static void Main()
		{
			ReadConfig();

			builds.CollectionChanged += Builds_CollectionChanged;
			loads.CollectionChanged += Loads_CollectionChanged;
			BuildsProgressTo += Program_BuildsProgressTo;

			if (Program.Settings.InternalServer)
				master.Start();
			if (Program.Settings.WatchSave)
				watchSave();

			RuneLog.logTo = new progLogger();

			Application.EnableVisualStyles();
			Application.SetCompatibleTextRenderingDefault(false);
			Application.Run(new Main());
		}

		private static void Program_BuildsProgressTo(object sender, PrintToEventArgs e)
		{
			log.Info("@" + e.Message);
		}

		public static void ReadConfig()
		{
			if (config != null)
			{
				// it's stored as string, what is fasted yescompare?
				// this?
				/*
				if (config.AppSettings.Settings.AllKeys.Contains("nostats"))
				{
					bool tstats;
					if (bool.TryParse(config.AppSettings.Settings["nostats"].Value, out tstats))
						makeStats = !tstats;
				}*/
			}
		}

		private static void Loads_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
		{
			switch (e.Action)
			{
				case System.Collections.Specialized.NotifyCollectionChangedAction.Add:
				case System.Collections.Specialized.NotifyCollectionChangedAction.Reset:
					break;
				case System.Collections.Specialized.NotifyCollectionChangedAction.Remove:
					foreach (var l in e.OldItems.Cast<Loadout>())
					{
						foreach (Rune r in l.Runes.Where(r => r != null))
						{
							r.Locked = false;
						}
					}
					break;
				case System.Collections.Specialized.NotifyCollectionChangedAction.Replace:
				case System.Collections.Specialized.NotifyCollectionChangedAction.Move:
				default:
					throw new NotImplementedException();
			}
		}

		private static void Builds_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
		{
			switch (e.Action)
			{
				case System.Collections.Specialized.NotifyCollectionChangedAction.Add:
					foreach (var b in e.NewItems.Cast<Build>())
					{
						if (Program.data != null)
						{
							// for each build, find the build in the buildlist with the same mon name?
							//var bnum = buildList.Items.Cast<ListViewItem>().Select(it => it.Tag as Build).Where(d => d.MonName == b.MonName).Count();
							// if there is a build with this monname, maybe I have 2 mons with that name?!
							if (!System.Diagnostics.Debugger.IsAttached)
								Program.log.Debug("finding " + b.MonId);
							if (Program.data.GetMonster(b.MonId) != null)
							{
								b.mon = Program.data.GetMonster(b.MonId);
							}
							else
							{
								var bnum = builds.Count(bu => bu.MonName == b.MonName);
								b.mon = Program.data.GetMonster(b.MonName, bnum + 1);
							}
						}
						else
						{
							b.mon = new Monster();
							b.mon.Name = b.MonName;
						}
					}
					break;
				case System.Collections.Specialized.NotifyCollectionChangedAction.Remove:
				case System.Collections.Specialized.NotifyCollectionChangedAction.Replace:
				case System.Collections.Specialized.NotifyCollectionChangedAction.Move:
				case System.Collections.Specialized.NotifyCollectionChangedAction.Reset:
				default:
					throw new NotImplementedException();
			}
		}
		
		/// <summary>
		/// Checks the Working directory for a supported save file
		/// </summary>
		public static LoadSaveResult FindSave()
		{
			string[] files;
			if (!string.IsNullOrWhiteSpace(Settings.SaveLocation) && File.Exists(Settings.SaveLocation))
			{
				return LoadSave(Settings.SaveLocation);
			}
			else if ((files = Directory.GetFiles(Environment.CurrentDirectory, "*-swarfarm.json")).Any())
			{
				if (files.Count() > 1)
					return LoadSaveResult.FileNotFound;
				return LoadSave(files.First());
			}
			else if (File.Exists("save.json"))
			{
				return LoadSave("save.json");
			}
			return LoadSaveResult.FileNotFound;
		}

		public static LoadSaveResult LoadSave(string filename)
		{
			if (string.IsNullOrWhiteSpace(filename))
			{
				log.Error("Filename for save is null");
				return LoadSaveResult.FileNotFound;
			}
			if (!File.Exists(filename))
			{
				log.Error($"File {filename} doesn't exist");
				return LoadSaveResult.FileNotFound;
			}
			log.Info("Loading " + filename + " as save.");
			string text = File.ReadAllText(filename);

			try
			{
				Program.data = JsonConvert.DeserializeObject<Save>(text);
				
				// TODO: trash
				for (int i = 0; i < Deco.ShrineStats.Length; i++)
				{
					var stat = Deco.ShrineStats[i];
					
					if (DoesSettingExist("shrine" + stat))
					{
						int val = (int)Settings["shrine" + stat];
						Program.data.shrines[stat] = val;
						int level = (int)Math.Floor(val / Deco.ShrineLevel[i]);
					}
				}
			}
			catch (Exception e)
			{
				File.WriteAllText("error_save.txt", e.ToString());
				throw new Exception("Error occurred loading Save JSON.\r\n" + e.GetType() + "\r\nInformation is saved to error_save.txt");
			}
			return LoadSaveResult.Success;//text.Length;
		}

		private static void watchSave()
		{
			if (saveFileWatcher == null)
			{
				saveFileWatcher = new FileSystemWatcher();
				saveFileWatcher.Changed += SaveFileWatcher_Changed;
			}
			saveFileWatcher.Path = Path.GetDirectoryName(Program.Settings.SaveLocation);
			saveFileWatcher.Filter = Path.GetFileName(Program.Settings.SaveLocation);
			saveFileWatcher.NotifyFilter = NotifyFilters.LastWrite;

		}

		private static void SaveFileWatcher_Changed(object sender, FileSystemEventArgs e)
		{
			if (saveFileDebouncer == null)
			{
				saveFileDebouncer = new System.Timers.Timer();
				saveFileDebouncer.Elapsed += SaveFileDebouncer_Elapsed;
				saveFileDebouncer.AutoReset = false;
			}
			saveFileDebouncer.Interval = 500;
			saveFileDebouncer.Stop();
			saveFileDebouncer.Start();
		}

		private static void SaveFileDebouncer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
		{
			if (saveFileTouched != null && saveFileTouched.GetInvocationList().Length > 0)
			{
				saveFileTouched.Invoke(Program.Settings.SaveLocation, new EventArgs());
			}
			else
			{
				LoadSave(Program.Settings.SaveLocation);
			}
		}

		public static LoadSaveResult LoadBuilds(string filename = "builds.json")
		{
			if (!File.Exists(filename))
			{
				log.Error($"{filename} wasn't found.");
				return LoadSaveResult.FileNotFound;
			}
			log.Info($"Loading {filename} as builds.");

			try
			{
				var bstr = File.ReadAllText(filename);

				// upgrade:

				bstr = bstr.Replace("\"b_hp\"", "\"hp\"");
				bstr = bstr.Replace("\"b_atk\"", "\"atk\"");
				bstr = bstr.Replace("\"b_def\"", "\"def\"");
				bstr = bstr.Replace("\"b_spd\"", "\"spd\"");
				bstr = bstr.Replace("\"b_crate\"", "\"critical_rate\"");
				bstr = bstr.Replace("\"b_cdmg\"", "\"critical_damage\"");
				bstr = bstr.Replace("\"b_acc\"", "\"accuracy\"");
				bstr = bstr.Replace("\"b_res\"", "\"res\"");

				var bs = JsonConvert.DeserializeObject<List<Build>>(bstr);
				foreach (var b in bs.OrderBy(b => b.priority))
				{
					builds.Add(b);
				}
			}
			catch (Exception e)
			{
				File.WriteAllText("error_build.txt", e.ToString());
				throw new Exception("Error occurred loading Build JSON.\r\n" + e.GetType() + "\r\nInformation is saved to error_build.txt");
			}
		
			if (Program.builds.Count > 0 && (Program.data?.Monsters == null))
			{
				// backup, just in case
				string destFile = Path.Combine("", string.Format("{0}.backup{1}", "builds", ".json"));
				int num = 2;
				while (File.Exists(destFile))
				{
					destFile = Path.Combine("", string.Format("{0}.backup{1}{2}", "builds", num, ".json"));
					num++;
				}

				File.Copy("builds.json", destFile);
				return LoadSaveResult.Failure;
			}

			SanitizeBuilds();

			return LoadSaveResult.Success;
		}
		
		public static void SanitizeBuilds()
		{
			int current_pri = 1;
			foreach (Build b in Program.builds.OrderBy(bu => bu.priority))
			{
				int id = b.ID;
				if (b.ID == 0 || Program.builds.Where(bu => bu != b).Select(bu => bu.ID).Any(bid => bid == b.ID))
				{
					//id = buildList.Items.Count + 1;
					id = 1;
					while (Program.builds.Any(bu => bu.ID == id))
						id++;
					b.ID = id;
				}
				b.priority = current_pri++;

				// make sure bad things are removed
				foreach (var ftab in b.runeFilters)
				{
					foreach (var filter in ftab.Value)
					{
						if (filter.Key == "SPD")
							filter.Value.Percent = null;
						if (filter.Key == "ACC" || filter.Key == "RES" || filter.Key == "CR" || filter.Key == "CD")
							filter.Value.Flat = null;
					}
				}

				// upgrade builds, hopefully
				while (b.VERSIONNUM < Create.VERSIONNUM)
				{
					switch (b.VERSIONNUM)
					{
						case 0: // unversioned to 1
							b.Threshold = b.Maximum;
							b.Maximum = new Stats();
							break;

					}
					b.VERSIONNUM++;
				}
			}
		}

		public static LoadSaveResult SaveBuilds(string filename = "builds.json")
		{
			log.Info($"Saving builds to {filename}");
			// TODO: fix this mess
			//Program.builds.Clear();

			//var lbs = buildList.Items;

			foreach (Build bb in builds)
			{
				if (bb.mon != null && bb.mon.Name != "Missingno")
				{
					if (!bb.DownloadAwake || Program.data.GetMonster(bb.mon.Name).Name != "Missingno")
					{
						bb.MonName = bb.mon.Name;
						bb.MonId = bb.mon.Id;
					}
					else
					{
						if (Program.data.GetMonster(bb.mon.Id).Name != "Missingno")
						{
							bb.MonId = bb.mon.Id;
							bb.MonName = Program.data.GetMonster(bb.mon.Id).Name;
						}
					}
				}
				//Program.builds.Add(bb);
			}

			// only write if there are builds, may save some files
			if (Program.builds.Count > 0)
			{
				try
				{
					// keep a single recent backup
					if (File.Exists(filename))
						File.Copy(filename, filename + ".backup", true);
					var str = JsonConvert.SerializeObject(Program.builds, Formatting.Indented);
					File.WriteAllText(filename, str);
					return LoadSaveResult.Success;
				}
				catch (Exception e)
				{
					log.Error($"Error while saving builds {e.GetType()}", e);
					throw;
					//MessageBox.Show(e.ToString());
				}
			}
			return LoadSaveResult.Failure;
		}

		public static LoadSaveResult SaveLoadouts(string filename = "loads.json")
		{
			log.Info($"Saving loads to {filename}");

			if (loads.Count > 0)
			{
				try
				{
					// keep a single recent backup
					if (File.Exists(filename))
						File.Copy(filename, filename + ".backup", true);
					var str = JsonConvert.SerializeObject(loads);
					File.WriteAllText(filename, str);
					return LoadSaveResult.Success;
				}
				catch (Exception e)
				{
					log.Error($"Error while saving loads {e.GetType()}", e);
					throw;
					//MessageBox.Show(e.ToString());
				}
				return LoadSaveResult.Failure;
			}
			return LoadSaveResult.EmptyFile;
		}

		public static LoadSaveResult LoadLoadouts(string filename = "loads.json")
		{
			try
			{
				string text = File.ReadAllText(filename);
				var lloads = JsonConvert.DeserializeObject<Loadout[]>(text);
				loads.Clear();

				foreach (var load in lloads)
				{
					for (int i = 0; i < 6; i++)
					{
						load.Runes[i] = Program.data.Runes.FirstOrDefault(r => r.Id == load.RuneIDs[i]);
						if (load.Runes[i] != null)
						{
							load.Runes[i].Locked = true;
							foreach (var ms in load.manageStats[i])
								load.Runes[i].manageStats.AddOrUpdate(ms.Key, ms.Value, (s, d) => ms.Value);
						}
					}
					load.Shrines = data.shrines;
					loads.Add(load);
				}
				return LoadSaveResult.Success;
			}
			catch (Exception e)
			{
				log.Error($"Error while loading loads {e.GetType()}", e);
				//MessageBox.Show("Error occurred loading Save JSON.\r\n" + e.GetType() + "\r\nInformation is saved to error_save.txt");
				File.WriteAllText("error_loads.txt", e.ToString());
				throw;
			}
			return LoadSaveResult.Failure;
		}

		internal static void ClearLoadouts()
		{
			foreach (Loadout l in loads)
			{
				foreach (Rune r in l.Runes)
				{
					if (r != null)
						r.Locked = false;
				}
			}
			loads.Clear();
		}

		public static void BuildPriority(Build build, int deltaPriority)
		{
			build.priority += deltaPriority;
			var bpri = builds.OrderBy(b => b.priority).ThenBy(b => b == build ? deltaPriority : 0).ToList();
			int i = 1;
			foreach (var b in bpri)
			{
				b.priority = i++;
			}
		}

		public static void RunTest(Build build, Action<Build, BuildResult> onFinish = null)
		{
			if (build.IsRunning)
				throw new InvalidOperationException("This build is already running");

			Task.Factory.StartNew(() =>
			{
				// Allow the window to draw before destroying the CPU
				Thread.Sleep(100);
				
				// Disregard locked, but honor equippedness checking
				build.RunesUseEquipped = Program.Settings.UseEquipped;
				build.RunesUseLocked = Program.Settings.LockTest;
				build.BuildGenerate = Program.Settings.TestGen;
				build.BuildTake = Program.Settings.TestShow;
				build.BuildTimeout = Program.Settings.TestTime;
				build.shrines = Program.data.shrines;
				build.BuildDumpBads = false;
				build.BuildGoodRunes = false;

				build.GenRunes(Program.data);
				var result = build.GenBuilds();

				onFinish?.Invoke(build, result);
			});
		}

		public static void StopBuild()
		{
			runSource?.Cancel();
		}

		public static void RunBuild(Build b, bool saveStats = false)
		{
			if (Program.data == null)
				return;

			if (currentBuild != null)
			{
				if (runTask != null && runTask.Status != TaskStatus.Running)
					throw new Exception("Already running builds!");
				else
				{
					runSource.Cancel();
					return;
				}
			}


			runSource = new CancellationTokenSource();
			runToken = runSource.Token;
			runTask = Task.Factory.StartNew(() =>
			{
				runBuild(b, saveStats);
			});
		}

		private static void runBuild(Build b, bool saveStats = false)
		{
			try
			{
				if (b == null)
				{
					log.Info("Build is null");
					return;
				}
				if (currentBuild != null)
					throw new InvalidOperationException("Already running a build");
				if (b.IsRunning)
					throw new InvalidOperationException("This build is already running");
				currentBuild = b;
				/*
				if (plsDie)
				{
					log.Info("Cancelling build " + b.ID + " " + b.MonName);
					//plsDie = false;
					return;
				}*/
				/*
				if (currentBuild != null)
				{
					log.Info("Force stopping " + currentBuild.ID + " " + currentBuild.MonName);
					currentBuild.isRun = false;
				}

				if (isRunning)
					log.Info("Looping...");

				while (isRunning)
				{
					plsDie = true;
					b.isRun = false;
					Thread.Sleep(100);
				}
				*/

				log.Info("Starting watch " + b.ID + " " + b.MonName);

				Stopwatch buildTime = Stopwatch.StartNew();
				//currentBuild = b;

				// TODO: what is this? Maybe it unlocks runes used if this build was already run?
				//ListViewItem[] olvs = null;
				//Invoke((MethodInvoker)delegate { olvs = loadoutList.Items.Find(b.ID.ToString(), false); });
				/*
				if (olvs.Length > 0)
				{
					var olv = olvs.First();
					Loadout ob = (Loadout)olv.Tag;
					foreach (Rune r in ob.Runes)
					{
						r.Locked = false;
					}
				}*/

				b.RunesUseLocked = false;
				b.RunesUseEquipped = Program.Settings.UseEquipped;
				b.BuildSaveStats = saveStats;
				b.BuildGoodRunes = false;
				b.GenRunes(Program.data);
				b.shrines = Program.data.shrines;

				#region Check enough runes
				string nR = "";
				for (int i = 0; i < b.runes.Length; i++)
				{
					if (b.runes[i] != null && b.runes[i].Length == 0)
						nR += (i + 1) + " ";
				}

				if (nR != "")
				{
					BuildsProgressTo?.Invoke(null, new PrintToEventArgs(b, ":( " + nR + "Runes"));
					return;
				}
				#endregion

				//isRunning = true;

				b.BuildGenerate = 0;
				b.BuildTake = 0;
				b.BuildTimeout = 0;
				b.BuildDumpBads = true;

				b.BuildPrintTo += BuildsProgressTo;

				b.BuildPrintTo += (bq, s) => {
					if (runToken.IsCancellationRequested)
						b.Cancel();
				};

				var result = b.GenBuilds();

				buildTime.Stop();
				b.Time = buildTime.ElapsedMilliseconds;
				log.Info("Stopping watch " + b.ID + " " + b.MonName + " @ " + buildTime.ElapsedMilliseconds);

				if (b.Best != null)
				{

					b.Best.Current.BuildID = b.ID;

					#region Get the rune diff
					b.Best.Current.powerup =
					b.Best.Current.upgrades =
					b.Best.Current.runesNew =
					b.Best.Current.runesChanged = 0;

					foreach (Rune r in b.Best.Current.Runes)
					{
						r.Locked = true;
						if (r.AssignedId != b.Best.Id)
						{
							if (r.IsUnassigned)
								b.Best.Current.runesNew++;
							else
								b.Best.Current.runesChanged++;
						}
						b.Best.Current.powerup += Math.Max(0, (b.Best.Current.FakeLevel[r.Slot - 1]) - r.Level);
						if (b.Best.Current.FakeLevel[r.Slot - 1] != 0)
						{
							int tup = (int)Math.Floor(Math.Min(12, (b.Best.Current.FakeLevel[r.Slot - 1])) / (double)3);
							int cup = (int)Math.Floor(Math.Min(12, r.Level) / (double)3);
							b.Best.Current.upgrades += Math.Max(0, tup - cup);
						}
					}
					#endregion

					//currentBuild = null;
					b.Best.Current.Time = b.Time;

					loads.Add(b.Best.Current);

					// if we are on the hunt of good runes.
					if (goodRunes && saveStats)
					{
						var theBest = b.Best;
						int count = 0;
						// we must progressively ban more runes from the build to find second-place runes.
						//GenDeep(b, 0, printTo, ref count);
						RunBanned(b, ++count, theBest.Current.Runes.Where(r => r.Slot % 2 != 0).Select(r => r.Id).ToArray());
						RunBanned(b, ++count, theBest.Current.Runes.Where(r => r.Slot % 2 == 0).Select(r => r.Id).ToArray());
						RunBanned(b, ++count, theBest.Current.Runes.Select(r => r.Id).ToArray());

						// after messing all that shit up
						b.Best = theBest;
					}
					

					#region Save Build stats
					
					/* TODO: put Excel on Program */
					if (saveStats)
					{
						runeSheet.StatsExcelBuild(b, b.mon, b.Best.Current, true);
					}

					// clean up for GC
					if (b.buildUsage != null)
						b.buildUsage.loads.Clear();
					if (b.runeUsage != null)
					{
						b.runeUsage.runesGood.Clear();
						b.runeUsage.runesUsed.Clear();
					}
					b.runeUsage = null;
					b.buildUsage = null;
					/**/
					#endregion
				}

				b.BuildPrintTo -= BuildsProgressTo;

				//if (plsDie)
				//    printTo?.Invoke("Canned");
				//else 
				if (b.Best != null)
					BuildsProgressTo?.Invoke(null, new PrintToEventArgs(b, "Done"));
				else
					BuildsProgressTo?.Invoke(null, new PrintToEventArgs(b, result + " :("));

				log.Info("Cleaning up");
				//b.isRun = false;
				//currentBuild = null;
			}
			catch (Exception e)
			{
				log.Error("Error during build " + b.ID + " " + e.Message + Environment.NewLine + e.StackTrace);
			}
			finally
			{
				currentBuild = null;
				log.Info("Cleaned");
			}
		}
		
		private static void RunBanned(Build b, int c, params ulong[] doneIds)
		{
			log.Info("Running ban");
			try
			{
				b.BanEmTemp(doneIds);

				b.RunesUseLocked = false;
				b.RunesUseEquipped = Program.Settings.UseEquipped;
				b.BuildSaveStats = true;
				b.BuildGoodRunes = goodRunes;
				b.GenRunes(Program.data);

				b.BuildTimeout = 0;
				b.BuildTake = 0;
				b.BuildGenerate = 0;
				b.BuildDumpBads = true;
				var result = b.GenBuilds($"{c} ");
				b.BuildGoodRunes = false;
				log.Info("ran ban with result: " + result);
			}
			catch (Exception ex)
			{
				log.Error("Running ban failed ", ex);
			}
			finally
			{
				b.BanEmTemp(new ulong[] { });
				b.BuildSaveStats = false;
				b.GenRunes(Program.data);
				log.Info("Ban finished");
			}
		}

		public static void RunBuilds(bool skipLoaded, int runTo = -1)
		{
			if (Program.data == null)
				return;

			if (isRunning)
			{
				if (runTask != null && runTask.Status != TaskStatus.Running)
					throw new Exception("Already running builds!");
				else
				{
					runSource.Cancel();
					return;
				}
			}
			isRunning = true;

			try
			{
				if (runTask != null && runTask.Status == TaskStatus.Running)
				{
					runSource.Cancel();
					//if (currentBuild != null)
					 //   currentBuild.isRun = false;
					//plsDie = true;
					isRunning = false;
					return;
				}
				//plsDie = false;

				List<int> loady = new List<int>();

				if (!skipLoaded)
				{
					ClearLoadouts();
					foreach (var r in Program.data.Runes)
					{
						r.manageStats.AddOrUpdate("buildScoreIn", 0, (k, v) => 0);
						r.manageStats.AddOrUpdate("buildScoreTotal", 0, (k, v) => 0);
					}
				}

				List<Build> toRun = new List<Build>();
				foreach (var build in builds.OrderBy(b => b.priority))
				{
					if ((!skipLoaded || !loads.Any(l => l.BuildID == build.ID)) && (runTo == -1 || build.priority < runTo))
						toRun.Add(build);
				}

				/*
				bool collect = true;
				int newPri = 1;
				// collect the builds
				List<ListViewItem> list5 = new List<ListViewItem>();
				foreach (ListViewItem li in buildList.Items)
				{
					li.SubItems[0].Text = newPri.ToString();
					(li.Tag as Build).priority = newPri++;

					if (loady.Contains((li.Tag as Build).ID))
						continue;

					if ((li.Tag as Build).ID == runTo)
						collect = false;

					if (collect)
						list5.Add(li);

					li.SubItems[3].Text = "";
				}
				*/

				runSource = new CancellationTokenSource();
				runToken = runSource.Token;
				runTask = Task.Factory.StartNew(() =>
				{
					if (Program.data.Runes != null && !skipLoaded)
					{
						foreach (Rune r in Program.data.Runes)
						{
							r.Swapped = false;
							r.ResetStats();
						}
					}

					foreach (Build bbb in toRun)
					{
						runBuild(bbb, Program.Settings.MakeStats);
						if (runToken.IsCancellationRequested)
							break;
					}

					if (!runToken.IsCancellationRequested && Program.Settings.MakeStats)
					{
						if (!skipLoaded)
							Program.runeSheet.StatsExcelRunes(true);
						try
						{
							Program.runeSheet.StatsExcelSave(true);
						}
						catch (Exception ex)
						{
							Console.WriteLine(ex);
						}
					}
					isRunning = false;
				}, runSource.Token);
			}
			catch (Exception e)
			{
				MessageBox.Show(e.Message + Environment.NewLine + e.StackTrace, e.GetType().ToString());
			}
		}


		#region Extension Methods
		public static bool IsConnected(this Socket socket)
		{
			try
			{
				return !(socket.Poll(1, SelectMode.SelectRead) && socket.Available == 0);
			}
			catch (SocketException) { return false; }
		}

		public static double StandardDeviation<T>(this IEnumerable<T> src, Func<T, double> selector)
		{
			double av = src.Where(p => Math.Abs(selector(p)) > 0.00000001).Average(selector);
			List<double> nls = new List<double>();
			foreach (var o in src.Where(p => Math.Abs(selector(p)) > 0.00000001))
			{
				nls.Add((selector(o) - av) * (selector(o) - av));
			}
			double avs = nls.Average();
			return Math.Sqrt(avs);
		}

		public static T MakeControl<T>(this Control.ControlCollection ctrlC, string name, string suff, int x, int y, int w = 40, int h = 20, string text = null)
			where T : Control, new()
		{
			T ctrl = new T()
			{
				Name = name + suff,
				Size = new Size(w, h),
				Location = new Point(x, y),
				Text = text
			};
			ctrlC.Add(ctrl);

			return ctrl;
		}

		//http://stackoverflow.com/a/77233
		public static void SetDoubleBuffered(this System.Windows.Forms.Control c)
		{
			//Taxes: Remote Desktop Connection and painting
			//http://blogs.msdn.com/oldnewthing/archive/2006/01/03/508694.aspx
			if (System.Windows.Forms.SystemInformation.TerminalServerSession)
				return;

			System.Reflection.PropertyInfo aProp =
				  typeof(System.Windows.Forms.Control).GetProperty(
						"DoubleBuffered",
						System.Reflection.BindingFlags.NonPublic |
						System.Reflection.BindingFlags.Instance);

			aProp.SetValue(c, true, null);
		}

		#endregion

	}
}
