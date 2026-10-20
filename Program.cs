using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KardsVox.Audio;
using KardsVox.Core;
using KardsVox.UI;
using NAudio.Wave;

[CompilerGenerated]
internal class Program
{
	[STAThread]
	private static int Main(string[] args)
	{
		if (!OperatingSystem.IsWindows())
		{
			Console.Error.WriteLine("KardsVox requires Windows.");
			return 1;
		}
		if (args.Length != 0)
		{
			try
			{
				Console.OutputEncoding = Encoding.UTF8;
				Console.Title = "KardsVox";
			}
			catch (IOException)
			{
			}
		}
		string runtimeRoot = AppContext.BaseDirectory;
		string dataRoot = FindDataRoot(runtimeRoot);
		using (SessionLog.TryStart(runtimeRoot))
		{
			Console.WriteLine("KARDS VOX build: " + System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(Program).Assembly.Location).ProductVersion);
			if (args != null && args.Length == 1 && args[0] == "--status")
			{
				Console.WriteLine("KARDS " + (GameWindowDetector.Exists() ? "Online" : "Offline"));
				return 0;
			}
			if (args != null && args.Length == 1 && args[0] == "--self-test")
			{
				StateMachineSelfTest.Run();
				return 0;
			}
			if (args != null && args.Length == 1 && args[0] == "--audio-self-test")
			{
				AudioPlaybackSelfTest.Run();
				return 0;
			}
			if (args != null && args.Length == 1 && args[0] == "--intro-self-test")
			{
				try
				{
					using IntroPlayer testPlayer = new();
					using MemoryMatchMonitor testMonitor = new(runtimeRoot, testPlayer, new JapanVoicePack(runtimeRoot));
					testMonitor.VerifyOpeningCancellation();
					return 0;
				}
				catch (Exception error) { Console.Error.WriteLine(error); return 1; }
			}
			if (args != null && args.Length == 1 && args[0] == "--runtime-self-test")
			{
				try
				{
				RuntimeObjectIndex.Verify();
				using IntroPlayer testPlayer = new();
				using MemoryMatchMonitor testMonitor = new(runtimeRoot, testPlayer, new JapanVoicePack(runtimeRoot));
				testMonitor.VerifyLinuxMonitoring();
				return 0;
				}
				catch (Exception error) { Console.Error.WriteLine(error); return 1; }
			}
			if (args != null && args.Length == 1 && args[0] == "--scan-kards-key")
			{
				try
				{
					IReadOnlyList<string> keys = ReadOnlyAesKeyScanner.ScanKards(Console.WriteLine);
					if (keys.Count == 0)
					{
						Console.Error.WriteLine("No AES-256 schedule found. Enter the KARDS main menu and try again.");
						return 2;
					}
					foreach (string key in keys)
					{
						Console.WriteLine("AES-256 candidate: " + key);
					}
					return 0;
				}
				catch (Exception ex2)
				{
					Console.Error.WriteLine(ex2.Message);
					return 1;
				}
			}
			if (args != null && args.Length == 1 && args[0] == "--probe-unreal")
			{
				try
				{
					using UnrealReadOnlyProbe probe = new UnrealReadOnlyProbe();
					probe.DumpCandidates(Console.Out);
					return 0;
				}
				catch (Exception ex3)
				{
					Console.Error.WriteLine(ex3.Message);
					return 1;
				}
			}
			if (args != null && args.Length == 1 && args[0] == "--find-namepool")
			{
				try
				{
					using UnrealReadOnlyProbe probe2 = new UnrealReadOnlyProbe();
					probe2.FindNamePool(Console.Out);
					return 0;
				}
				catch (Exception ex4)
				{
					Console.Error.WriteLine(ex4.Message);
					return 1;
				}
			}
			if (args != null && args.Length == 1 && args[0] == "--find-kards-objects")
			{
				try
				{
					using UnrealReadOnlyProbe probe3 = new UnrealReadOnlyProbe();
					probe3.FindKardsObjects(Console.Out);
					return 0;
				}
				catch (Exception ex5)
				{
					Console.Error.WriteLine(ex5.ToString());
					return 1;
				}
			}
			if (args != null && args.Length == 1 && args[0] == "--find-kards-fields")
			{
				try
				{
					using UnrealReadOnlyProbe probe4 = new UnrealReadOnlyProbe();
					probe4.FindKardsFields(Console.Out);
					return 0;
				}
				catch (Exception ex6)
				{
					Console.Error.WriteLine(ex6.ToString());
					return 1;
				}
			}
			if (args != null && args.Length == 2 && args[0] == "--search-unreal-names")
			{
				string searchText = args[1];
				try
				{
					using UnrealReadOnlyProbe probe5 = new UnrealReadOnlyProbe();
					probe5.SearchNames(searchText, Console.Out);
					return 0;
				}
				catch (Exception ex7)
				{
					Console.Error.WriteLine(ex7.ToString());
					return 1;
				}
			}
			if (args != null && args.Length == 1 && args[0] == "--find-object-registry")
			{
				try
				{
					using UnrealReadOnlyProbe probe6 = new UnrealReadOnlyProbe();
					probe6.FindObjectRegistry(Console.Out);
					return 0;
				}
				catch (Exception ex8)
				{
					Console.Error.WriteLine(ex8.ToString());
					return 1;
				}
			}
			if (args != null && args.Length == 2 && args[0] == "--dump-class-props")
			{
				string className = args[1];
				try
				{
					using UnrealReadOnlyProbe probe7 = new UnrealReadOnlyProbe();
					probe7.DumpClassProperties(className, Console.Out);
					return 0;
				}
				catch (Exception ex9)
				{
					Console.Error.WriteLine(ex9.ToString());
					return 1;
				}
			}
			if (args != null && args.Length == 2 && args[0] == "--dump-struct-address")
			{
				string addressText = args[1];
				try
				{
					string value;
					if (!addressText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
					{
						value = addressText;
					}
					else
					{
						string text = addressText;
						value = text.Substring(2, text.Length - 2);
					}
					long address = Convert.ToInt64(value, 16);
					using UnrealReadOnlyProbe probe8 = new UnrealReadOnlyProbe();
					probe8.DumpStructAddress(address, Console.Out);
					return 0;
				}
				catch (Exception ex10)
				{
					Console.Error.WriteLine(ex10.ToString());
					return 1;
				}
			}
			if (args != null && args.Length == 1 && args[0] == "--runtime-snapshot")
			{
				try
				{
					UnrealRuntimeReader runtime = new UnrealRuntimeReader();
					try
					{
						Console.WriteLine($"KARDS pid={runtime.ProcessId}; objects={runtime.ObjectCount:N0}");
						long[] boards = runtime.FindInstances(runtime.BoardClass).ToArray();
						long[] monitors = runtime.FindInstances(runtime.BattleMonitorClass).ToArray();
						Console.WriteLine("BP_Board instances: " + string.Join(", ", boards.Select((long x) => $"0x{x:X}({runtime.GetObjectName(x)})")));
						Console.WriteLine("BP_BattleStateMonitor instances: " + string.Join(", ", monitors.Select((long x) => $"0x{x:X}({runtime.GetObjectName(x)}, monitoring={runtime.ReadByte(x + 720)})")));
						long[] array = boards;
						foreach (long board in array)
						{
							(long, int, int) ids = runtime.ReadArray(board, 1280);
							(long, int, int) enemies = runtime.ReadArray(board, 1648);
							long hqLeft = runtime.ReadPointer(board + 712);
							long hqRight = runtime.ReadPointer(board + 720);
							Console.WriteLine($"Board 0x{board:X}: HQ=0x{hqLeft:X}/0x{hqRight:X}; playedIDs={ids.Item2}/{ids.Item3} data=0x{ids.Item1:X}; enemy={enemies.Item2}/{enemies.Item3}");
							Console.WriteLine($"  directHQ=0x{runtime.ReadPointer(board + 2024):X}/0x{runtime.ReadPointer(board + 2032):X}; cardRef=0x{runtime.ReadPointer(board + 1016):X}; tmpCurrent=0x{runtime.ReadPointer(board + 1264):X}; handRef=0x{runtime.ReadPointer(board + 2536):X}; placing={runtime.ReadByte(board + 2880)}; clicked={runtime.ReadByte(board + 3136)}; faction={runtime.ReadByte(board + 1987)}");
							long ownHqVisual = runtime.ReadPointer(board + 2024);
							long enemyHqVisual = runtime.ReadPointer(board + 2032);
							Console.WriteLine($"  directHQLocation={runtime.ReadByte(ownHqVisual + 944)}/{runtime.ReadByte(enemyHqVisual + 944)}; directHQSelf=0x{runtime.ReadPointer(ownHqVisual + 2056):X}/0x{runtime.ReadPointer(enemyHqVisual + 2056):X}");
							Console.WriteLine($"  introBack=0x{runtime.ReadPointer(board + 2816):X}/0x{runtime.ReadPointer(board + 2824):X}; introLocationHand=0x{runtime.ReadPointer(board + 2888):X}/0x{runtime.ReadPointer(board + 2896):X}; showHQ={runtime.ReadSingle(board + 792):F3}/{runtime.ReadByte(board + 796)}; battleHudShown={runtime.ReadByte(board + 3720)}; drawVisible={runtime.ReadByte(board + 1920)}; ordersFlying={runtime.ReadInt(board + 1916)}; playRunning={runtime.ReadByte(board + 1312)}; tmpId={runtime.ReadInt(board + 1316)}; tmpEnemy={runtime.ReadByte(board + 1664)}");
						}
						string[] array2 = new string[3] { "BP_BaseCard_C", "BP_HandCard_C", "BP_BoardCard_C" };
						foreach (string visualClassName in array2)
						{
							long? visualClass = runtime.GetBlueprintClass(visualClassName);
							if (visualClass.HasValue)
							{
								long[] visuals = runtime.FindInstances(visualClass.Value).Where(delegate(long x)
								{
									string? objectName = runtime.GetObjectName(x);
									return objectName != null && !objectName.StartsWith("Default__", StringComparison.Ordinal);
								}).Take(40)
									.ToArray();
								Console.WriteLine($"{visualClassName} instances: {visuals.Length}");
								array = visuals;
								foreach (long visual in array)
								{
									long cardRef = runtime.ReadPointer(visual + 2056);
									long sound = runtime.ReadPointer(cardRef + 832);
									Console.WriteLine($"  0x{visual:X} name={runtime.GetObjectName(visual)} selfRef=0x{cardRef:X}({runtime.GetObjectName(cardRef)}/{runtime.GetClassName(cardRef)}) classPtr=0x{runtime.ReadPointer(cardRef + 16):X} nameId=0x{runtime.ReadInt(cardRef + 24):X} sound=0x{sound:X}({runtime.GetObjectName(sound)}), side={runtime.ReadByte(cardRef + 630)}, def={runtime.ReadInt(cardRef + 116)}, loc={runtime.ReadByte(visual + 944)}, hiddenBits=0x{runtime.ReadByte(visual + 88):X2}, role={runtime.ReadByte(visual + 360)}, hoverable={runtime.ReadByte(visual + 2592)}, blockDrag={runtime.ReadByte(visual + 2464)}, root=0x{runtime.ReadPointer(visual + 440):X}, i84={runtime.ReadInt(cardRef + 132)}, i88={runtime.ReadInt(cardRef + 136)}, i278={runtime.ReadInt(cardRef + 632)}, i310={runtime.ReadInt(cardRef + 784)}, i33c={runtime.ReadInt(cardRef + 828)}");
								}
							}
						}
						return 0;
					}
					finally
					{
						if (runtime != null)
						{
							((IDisposable)runtime).Dispose();
						}
					}
				}
				catch (Exception ex11)
				{
					Console.Error.WriteLine(ex11.ToString());
					return 1;
				}
			}
			if (args != null && args.Length == 2 && args[0] == "--analyze-image")
			{
				string imagePath = args[1];
				using Bitmap image = new Bitmap(imagePath);
				BuiltInBoardDetector.Evidence evidence = BuiltInBoardDetector.Analyze(image);
				EmptyTableTransitionDetector.Evidence transition = EmptyTableTransitionDetector.Analyze(image);
				Console.WriteLine($"Picture: {(evidence.IsBoard ? "battlefield" : (evidence.IsPreMatch ? "prematch" : "other"))}; turn={evidence.TurnButtonRun:F2}, HQ={evidence.UpperHqContrast:F0}/{evidence.LowerHqContrast:F0}, card={evidence.UpperHqFill:F2}/{evidence.LowerHqFill:F2}, Japanese units={JapaneseUnitDetector.CountMarkers(image)}, cutoff={evidence.BrightCutoff:F0}");
				Console.WriteLine($"Empty-table transition={transition.IsEmptyTable}; global={transition.GlobalLuma:F1}, center={transition.CenterLuma:F1}, wood={transition.CenterRed:F0}/{transition.CenterGreen:F0}/{transition.CenterBlue:F0}");
				return 0;
			}
			int num = args.Length;
			bool flag = (uint)(num - 2) <= 1u;
			if (flag && args[0] == "--replay-frames")
			{
				string framesDirectory = args[1];
				int repeats = 1;
				flag = args.Length == 3 && !int.TryParse(args[2], out repeats);
				if (!flag)
				{
					bool flag2 = ((repeats < 1 || repeats > 10) ? true : false);
					flag = flag2;
				}
				if (flag)
				{
					Console.Error.WriteLine("Replay repeat count must be 1 to 10.");
					return 1;
				}
				string[] frames = (Directory.Exists(framesDirectory) ? Directory.GetFiles(framesDirectory, "sample-*.jpg").OrderBy<string, string>((string path) => path, StringComparer.Ordinal).ToArray() : Array.Empty<string>());
				if (frames.Length == 0)
				{
					Console.Error.WriteLine("--replay-frames requires a directory containing sample-*.jpg screenshots.");
					return 1;
				}
				MatchStateMachine machine = new MatchStateMachine(new DetectionSettings());
				JapaneseUnitDetector units = new JapaneseUnitDetector();
				DateTimeOffset start = DateTimeOffset.UtcNow;
				for (int index = 0; index < frames.Length * repeats; index++)
				{
					using Bitmap frame = new Bitmap(frames[index % frames.Length]);
					BuiltInBoardDetector.Evidence evidence2 = BuiltInBoardDetector.Analyze(frame);
					MatchState before = machine.State;
					DateTimeOffset now = start.AddMilliseconds(index * 400);
					bool intro = machine.Observe(evidence2.IsBoard, evidence2.IsPreMatch, now);
					if (machine.State != before)
					{
						MatchState state = machine.State;
						if ((state == MatchState.OutsideMatch || (uint)(state - 2) <= 1u) ? true : false)
						{
							units.Reset();
						}
						Console.WriteLine($"{(double)index * 0.4:F1}s {before} -> {machine.State}");
					}
					if (intro)
					{
						Console.WriteLine($"{(double)index * 0.4:F1}s INTRO");
					}
					if (evidence2.IsBoard && machine.State == MatchState.InMatch && units.Observe(frame, now))
					{
						Console.WriteLine($"{(double)index * 0.4:F1}s DEPLOY (Japanese units: {units.ConfirmedCount})");
					}
				}
				return 0;
			}
			if (args != null && args.Length == 2 && args[0] == "--record-visual")
			{
				string secondsValue = args[1];
				flag = !int.TryParse(secondsValue, out var seconds);
				if (!flag)
				{
					bool flag2 = ((seconds < 10 || seconds > 900) ? true : false);
					flag = flag2;
				}
				if (flag)
				{
					Console.Error.WriteLine("--record-visual requires 10 to 900 seconds.");
					return 1;
				}
				try
				{
					JapanVoicePack voices = new JapanVoicePack(runtimeRoot);
					using IntroPlayer player = new IntroPlayer();
					using MatchMonitor monitor = new MatchMonitor(dataRoot, player, voices, recordOnly: true);
					monitor.Start();
					Thread.Sleep(TimeSpan.FromSeconds(seconds));
					Console.WriteLine("Visual recording complete.");
					return 0;
				}
				catch (Exception ex12)
				{
					Console.Error.WriteLine(ex12.Message);
					return 1;
				}
			}
			if (args != null && args.Length == 1 && args[0] == "--voice-check")
			{
				try
				{
					JapanVoicePack voices2 = new JapanVoicePack(runtimeRoot);
					double totalSeconds = 0.0;
					foreach (string line in File.ReadLines(Path.Combine(runtimeRoot, "Data", "Japan", "voice_lines.tsv")).Skip(1))
					{
						if (!string.IsNullOrWhiteSpace(line))
						{
							JapanVoiceLine entry = voices2.ById(line.Split('\t')[0]);
							using WaveFileReader wav = new WaveFileReader(entry.Path);
							if (wav.WaveFormat.Encoding != WaveFormatEncoding.Pcm || wav.WaveFormat.SampleRate != 48000 || wav.WaveFormat.Channels != 1 || wav.WaveFormat.BitsPerSample != 16 || wav.TotalTime.TotalSeconds < 0.3)
							{
								throw new InvalidDataException("Invalid voice clip: " + entry.Path);
							}
							totalSeconds += wav.TotalTime.TotalSeconds;
						}
					}
					string[] array2 = JapanVoicePack.DeploymentPoolNames;
					foreach (string category in array2)
					{
						int size = voices2.GetDeploymentPoolSize(category);
						HashSet<string> cycle = new HashSet<string>(StringComparer.Ordinal);
						string previous = string.Empty;
						for (int index2 = 0; index2 < size; index2++)
						{
							previous = voices2.PickDeployment(category).Id;
							if (!cycle.Add(previous))
							{
								throw new InvalidDataException("Shuffle bag repeated before exhausting " + category + ".");
							}
						}
						string next = voices2.PickDeployment(category).Id;
						if (size > 1 && next == previous)
						{
							throw new InvalidDataException("Shuffle bag repeated at the cycle boundary for " + category + ".");
						}
						Console.WriteLine($"  {category}: {size} exclusive deployment lines, shuffle bag OK");
					}
					HashSet<string> orderCycle = new HashSet<string>(StringComparer.Ordinal);
					for (int index3 = 0; index3 < voices2.OrderPoolSize; index3++)
					{
						if (!orderCycle.Add(voices2.PickOrder().Id))
						{
							throw new InvalidDataException("Order shuffle bag repeated before exhaustion.");
						}
					}
					foreach (KeyValuePair<string, string> expected in new Dictionary<string, string>(StringComparer.Ordinal)
					{
						["card_event_desperate_measures_C"] = "玉碎",
						["card_event_surprise_attack_C"] = "奇袭命令",
						["card_event_sneak_attack_C"] = "奇袭命令",
						["card_event_air_blitz_C"] = "航空命令",
						["card_event_lightning_conquest_new_C"] = "航空命令",
						["card_event_imperial_order_C"] = "帝国指令",
						["card_event_blade_of_the_samurai_C"] = "武士之刃",
						["card_event_great_expanse_C"] = "扩张",
						["card_event_for_prosperety_C"] = "为了繁荣"
					})
					{
						OrderVoiceProfile actual = voices2.ResolveOrder(expected.Key);
						if (actual.Name != expected.Value)
						{
							throw new InvalidDataException("Order profile mismatch: " + expected.Key + " -> " + actual.Name);
						}
					}
					string[] orderResources = File.ReadLines(Path.Combine(runtimeRoot, "Data", "Japan", "card_catalog.tsv"))
						.Skip(1).Select(row => row.Split('\t')).Where(columns => columns.Length == 2 && columns[1] == "ORDER")
						.Select(columns => columns[0]).ToArray();
					if (voices2.OrderSubtitleCount != 124 || orderResources.Length != 124)
					{
						throw new InvalidDataException($"Order subtitle catalog mismatch: {voices2.OrderSubtitleCount}/124");
					}
					foreach (string resource in orderResources)
					{
						JapanOrderSubtitle subtitle = voices2.ResolveOrderSubtitle(resource + "_C");
						if (subtitle.Resource == "unknown" || string.IsNullOrWhiteSpace(subtitle.Title) ||
							string.IsNullOrWhiteSpace(subtitle.Line) || string.IsNullOrWhiteSpace(subtitle.Japanese))
							throw new InvalidDataException("Missing order subtitle: " + resource);
					}
					Console.WriteLine($"  ORDER: {voices2.OrderSubtitleCount} card-specific subtitles, synthesized telegraph only");
					foreach (string category2 in JapanVoicePack.DeploymentPoolNames)
					{
						if (voices2.GetHurtPoolSize(category2) == 0)
						{
							throw new InvalidDataException("Empty Japanese hurt voice pool: " + category2);
						}
					}
					Console.WriteLine("  HURT: category voice pools enabled, no preceding grunt effect");
					if (voices2.JapanCardCount != 312)
					{
						throw new InvalidDataException($"Japan card catalog mismatch: {voices2.JapanCardCount}/312");
					}
					foreach (KeyValuePair<string, string> expected2 in new Dictionary<string, string>(StringComparer.Ordinal)
					{
						["card_event_imperial_order_C"] = "ORDER",
						["card_event_blade_of_the_samurai_C"] = "ORDER",
						["card_event_great_expanse_C"] = "ORDER",
						["card_event_for_prosperety_C"] = "ORDER",
						["card_unit_zero_a6m2_21_C"] = "AIR",
						["card_unit_type_97_kai_C"] = "TANK",
						["card_unit_2nd_raiding_brigade_C"] = "INFANTRY"
					})
					{
						if (voices2.ClassifyJapanCard(expected2.Key) != expected2.Value)
						{
							throw new InvalidDataException("Japan card category mismatch: " + expected2.Key);
						}
					}
					NationCardCatalog sovietCards = new NationCardCatalog(runtimeRoot, VoicePackRegistry.Soviet, 319);
					NationCardCatalog japanCards = new NationCardCatalog(runtimeRoot, VoicePackRegistry.Japan, 312);
					if (sovietCards.Classify("card_unit_t_34_C") != "TANK" ||
						sovietCards.Classify("card_unit_ilyushin_il_2_C") != "AIR" ||
						sovietCards.Classify("card_unit_152mm_howitzer_gun_C") != "ARTILLERY" ||
						sovietCards.Classify("card_unit_52_k_C") != "ARTILLERY" ||
						sovietCards.Classify("card_unit_katyusha_C") != "ARTILLERY" ||
						sovietCards.Classify("card_unit_cossack_cavalry_C") != "CAVALRY" ||
						sovietCards.Classify("card_event_assault_C") != "ORDER" ||
						sovietCards.Classify("card_location_soviet_scen1_C") != null ||
						!sovietCards.Contains("card_location_soviet_scen1_C") ||
						japanCards.Contains("card_unit_t_34_C") || sovietCards.Contains("card_unit_zero_a6m2_21_C"))
						throw new InvalidDataException("Soviet card category mismatch.");
					string[] sovietResources = File.ReadLines(Path.Combine(runtimeRoot, "Data", "Soviet", "card_catalog.tsv"))
						.Skip(1).Select(row => row.Split('\t')[0]).ToArray();
					string[] japanResources = File.ReadLines(Path.Combine(runtimeRoot, "Data", "Japan", "card_catalog.tsv"))
						.Skip(1).Select(row => row.Split('\t')[0]).ToArray();
					if (sovietResources.Any(resource => japanCards.Contains(resource)) ||
						japanResources.Any(resource => sovietCards.Contains(resource)))
						throw new InvalidDataException("Japan and Soviet resource names overlap; automatic routing is ambiguous.");
					SovietSubtitlePack sovietSubtitles = new SovietSubtitlePack(runtimeRoot);
					AlliedOrderCatalog alliedOrders = new AlliedOrderCatalog(runtimeRoot);
					AlliedCardCatalog alliedCards = new AlliedCardCatalog(runtimeRoot);
					if (alliedCards.Count != 1309 ||
						!alliedCards.TryGet(out AlliedCardLabel americanUnit, "card_unit_104th_infantry_regiment_C") ||
						americanUnit.Origin != "USA" || americanUnit.Kind != "UNIT" ||
						!alliedCards.TryGet(out AlliedCardLabel germanUnit, "card_unit_101_infantry_regiment_C") ||
						germanUnit.Origin != "Germany" ||
						sovietResources.Any(resource => alliedCards.TryGet(out _, resource)) ||
						japanResources.Any(resource => alliedCards.TryGet(out _, resource)))
						throw new InvalidDataException("Allied card nation catalogue is incomplete or overlaps Japan/Soviet.");
					if (alliedOrders.Count != 510 ||
						!alliedOrders.TryGet(out AlliedOrderLabel unity, "card_event_unity_is_strength_C") ||
						unity.ChineseTitle != "团结就是力量" ||
						!alliedCards.TryGet(out AlliedCardLabel unityCard, unity.Resource) ||
						unityCard.Origin != "USA" || unityCard.OriginalTitle != "UNITY IS STRENGTH")
						throw new InvalidDataException("Allied order localization is missing Unity is Strength.");
					foreach (string pool in new[] { "INFANTRY", "TANK", "AIR", "ARTILLERY", "CAVALRY" })
						if (sovietSubtitles.GetUnitPoolSize(pool) < 3 || sovietSubtitles.GetUnitPoolSize(pool, "HURT") < 2 ||
							sovietSubtitles.GetUnitPoolSize(pool, "ATTACK") < 2)
							throw new InvalidDataException("Incomplete Soviet unit subtitle pool: " + pool);
					foreach (string pool in JapanVoicePack.DeploymentPoolNames)
						if (voices2.PickAttack(pool).Event != "ATTACK")
							throw new InvalidDataException("Incomplete Japanese attack voice pool: " + pool);
					if (voices2.PickAttack("ARTILLERY").Voice != "ARTY")
						throw new InvalidDataException("Japanese artillery attack must use artillery speech.");
					if (voices2.PickMove("INFANTRY").Event != "MOVE" ||
						voices2.PickMove("TANK").Event != "MOVE" ||
						string.IsNullOrWhiteSpace(voices2.PickMove("AIR").Japanese) ||
						string.IsNullOrWhiteSpace(voices2.PickMove("NAVY").Japanese))
						throw new InvalidDataException("Japanese frontline movement voice lines are missing.");
					int artilleryPoolSize = sovietSubtitles.GetUnitPoolSize("ARTILLERY");
					string[] artilleryCycle = Enumerable.Range(0, artilleryPoolSize)
						.Select(_ => sovietSubtitles.Pick("ARTILLERY", "DEPLOY").Original).ToArray();
					if (artilleryCycle.Distinct(StringComparer.Ordinal).Count() != artilleryPoolSize ||
						sovietSubtitles.Pick("ARTILLERY", "DEPLOY").Original == artilleryCycle[^1])
						throw new InvalidDataException("Soviet artillery subtitle pool repeated before cycling.");
					string[] sovietOrders = sovietResources.Where(resource => sovietCards.Classify(resource) == "ORDER").ToArray();
					if (sovietSubtitles.AudioCount != 174 || sovietSubtitles.IntroLines.Count != 5 ||
						sovietSubtitles.IntroLines.Any(line => string.IsNullOrWhiteSpace(line.Original) || string.IsNullOrWhiteSpace(line.Chinese)) ||
						sovietSubtitles.OrderCount != 119 || sovietOrders.Length != 119 ||
						sovietOrders.Any(resource =>
						string.IsNullOrWhiteSpace(sovietSubtitles.Pick("ORDER", "DEPLOY", resource + "_C").Original)) ||
						sovietSubtitles.Pick("ORDER", "DEPLOY", "card_event_assault_C").Chinese ==
						sovietSubtitles.Pick("ORDER", "DEPLOY", "card_event_arctic_convoy_C").Chinese)
						throw new InvalidDataException("Soviet subtitle mapping failed.");
					using (IntroPlayer routingPlayer = new IntroPlayer())
					using (MemoryMatchMonitor routingMonitor = new MemoryMatchMonitor(runtimeRoot, routingPlayer, voices2))
					{
						routingMonitor.VerifyAutomaticRouting();
						routingMonitor.VerifyFileBasedPacks();
						routingPlayer.VerifyFades(voices2.ById("JP_TANK_ATTACK_01").Path);
					}
					SpeechPauseMap.Verify();
					Console.WriteLine("Speech transition OK: quiet-gap detection, bounded wait, audio fade-out/in and volume reset.");
					Console.WriteLine("Automatic per-card nation routing OK: Japanese and Soviet voice follows each card; other allied cards keep their own national subtitles.");
					Console.WriteLine($"Soviet voice pack OK: {sovietSubtitles.AudioCount} Russian clips, {sovietSubtitles.OrderCount} individual voiced orders, five rotating unit pools including artillery.");
					Console.WriteLine($"Japan voice pack OK: {voices2.Count} clips, {totalSeconds:F1} seconds, {voices2.JapanCardCount} Japan card resources mapped.");
					return 0;
				}
				catch (Exception ex13)
				{
					Console.Error.WriteLine(ex13.Message);
					return 1;
				}
			}
			if (args != null && (args.Length == 2 || args.Length == 3) && args[0] == "--ui-snapshot")
			{
				string snapshotPath = args[1];
				string snapshotSettingsPath = Path.Combine(dataRoot, "Data", "detection.json");
				string? originalSettings = args.Length == 3 && File.Exists(snapshotSettingsPath)
					? File.ReadAllText(snapshotSettingsPath) : null;
				try
				{
					ApplicationConfiguration.Initialize();
					JapanVoicePack voices3 = new JapanVoicePack(runtimeRoot);
					using IntroPlayer player2 = new IntroPlayer();
					using MemoryMatchMonitor monitor2 = new MemoryMatchMonitor(dataRoot, player2, voices3);
					if (args.Length == 3)
					{
						monitor2.SetVoicePack(args[2]);
						if (DetectionSettings.Load(snapshotSettingsPath).SelectedVoicePack != monitor2.SelectedVoicePack)
							throw new InvalidDataException("Voice pack selection was not saved.");
					}
					using SubtitleOverlay overlay = new SubtitleOverlay();
					using TrainerForm form = new TrainerForm(monitor2, overlay);
					form.StartPosition = FormStartPosition.Manual;
					form.Location = new Point(-10000, -10000);
					form.ShowInTaskbar = false;
					form.Show();
					Application.DoEvents();
					using Bitmap bitmap = new Bitmap(form.Width, form.Height);
					form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
					string output = Path.GetFullPath(snapshotPath);
					Directory.CreateDirectory(Path.GetDirectoryName(output));
					bitmap.Save(output, ImageFormat.Png);
					form.Close();
					Console.WriteLine("UI snapshot saved: " + output);
					return 0;
				}
				catch (Exception ex14)
				{
					Console.Error.WriteLine(ex14.ToString());
					return 1;
				}
				finally
				{
					if (originalSettings != null) File.WriteAllText(snapshotSettingsPath, originalSettings);
				}
			}
			if (args != null && args.Length == 2 && args[0] == "--layout-check")
			{
				try
				{
					ApplicationConfiguration.Initialize();
					SubtitleOverlay.VerifyNationLayout(runtimeRoot, Path.GetFullPath(args[1]));
					return 0;
				}
				catch (Exception error) { Console.Error.WriteLine(error); return 1; }
			}
			if (args != null && (args.Length == 2 || args.Length == 3) && args[0] == "--subtitle-snapshot")
			{
				string subtitleSnapshotPath = args[1];
				try
				{
					ApplicationConfiguration.Initialize();
					using SubtitleOverlay overlay2 = new SubtitleOverlay
					{
						Opacity = 0.01,
						TransparencyKey = Color.Empty
					};
					_ = overlay2.Handle;
					string preview = args.Length == 3 ? args[2] : VoicePackRegistry.Japan;
					if (preview is "CombatFadeOut" or "CombatFadeIn")
					{
						bool entering = preview == "CombatFadeIn";
						if (entering)
						{
							var previous = new SovietSubtitlePack(runtimeRoot).Pick("INFANTRY", "HURT");
							overlay2.ShowCue(new MemoryMatchMonitor.SubtitleCue(previous.Chinese, TimeSpan.FromSeconds(4),
								false, Speaker: previous.Speaker, Original: previous.Original));
							overlay2.BeginTransition(TimeSpan.FromMilliseconds(180));
							Thread.Sleep(190);
							Application.DoEvents();
						}
						var line = new SovietSubtitlePack(runtimeRoot).Pick("INFANTRY", entering ? "ATTACK" : "HURT");
						overlay2.ShowCue(new MemoryMatchMonitor.SubtitleCue(line.Chinese, TimeSpan.FromSeconds(4),
							false, Speaker: line.Speaker, Original: line.Original, FadeIn: entering));
						if (!entering) overlay2.BeginTransition(TimeSpan.FromMilliseconds(180));
						Thread.Sleep(90);
					}
					else if (VoicePackRegistry.All.FirstOrDefault(p => p.FileBased && preview.StartsWith(p.Id, StringComparison.OrdinalIgnoreCase)) is { } definition)
					{
						var pack = new NationSubtitlePack(runtimeRoot, definition);
						string mode = preview[definition.Id.Length..];
						var line = mode == "Long" ? pack.AllLines.MaxBy(l => l.Original.Length)!
							: mode == "Order" ? pack.Pick("ORDER", "DEPLOY", pack.Cards.Resources.First(p => p.Value == "ORDER").Key)
							: mode == "Intro" ? pack.IntroLines[1] : pack.Pick("INFANTRY", "DEPLOY");
						overlay2.ShowCue(new MemoryMatchMonitor.SubtitleCue(line.Chinese, line.ReadingDuration,
							mode == "Intro", Speaker: line.Speaker, Original: line.Original));
					}
					else if (preview.Equals("JapanOrder", StringComparison.OrdinalIgnoreCase))
					{
						JapanOrderSubtitle order = new JapanVoicePack(runtimeRoot).ResolveOrderSubtitle("card_event_amphibious_assault_C");
						overlay2.ShowCue(new MemoryMatchMonitor.SubtitleCue(order.Title + "｜" + order.Line,
							TimeSpan.FromSeconds(4), Commander: false, Speaker: "通信兵", Original: order.Japanese));
					}
					else if (preview.Equals("SovietOrder", StringComparison.OrdinalIgnoreCase))
					{
						var line = new SovietSubtitlePack(runtimeRoot).Pick("ORDER", "DEPLOY", "card_event_arctic_convoy_C");
						overlay2.ShowCue(new MemoryMatchMonitor.SubtitleCue(line.Chinese, TimeSpan.FromSeconds(5),
							Commander: false, Speaker: line.Speaker, Original: line.Original));
					}
					else if (preview.Equals("SovietArtillery", StringComparison.OrdinalIgnoreCase))
					{
						var line = new SovietSubtitlePack(runtimeRoot).Pick("ARTILLERY", "DEPLOY");
						overlay2.ShowCue(new MemoryMatchMonitor.SubtitleCue(line.Chinese, TimeSpan.FromSeconds(4),
							Commander: false, Speaker: line.Speaker, Original: line.Original));
					}
					else if (preview.Equals("SovietFromPeople", StringComparison.OrdinalIgnoreCase))
					{
						var line = new SovietSubtitlePack(runtimeRoot).Pick("ORDER", "DEPLOY", "card_event_from_the_people_C");
						overlay2.ShowCue(new MemoryMatchMonitor.SubtitleCue(line.Chinese, TimeSpan.FromSeconds(5),
							Commander: false, Speaker: line.Speaker, Original: line.Original));
					}
					else if (preview.Equals("SovietLong", StringComparison.OrdinalIgnoreCase))
					{
						var line = new SovietSubtitlePack(runtimeRoot).IntroLines[2];
						overlay2.ShowCue(new MemoryMatchMonitor.SubtitleCue(line.Chinese, TimeSpan.FromSeconds(4),
							Commander: true, Speaker: "苏联指挥官", Original: line.Original));
					}
					else if (preview.Equals("SovietIntro", StringComparison.OrdinalIgnoreCase))
					{
						var lines = new SovietSubtitlePack(runtimeRoot).IntroLines
							.Select(line => new MemoryMatchMonitor.SubtitleLine(line.Original, line.Chinese)).ToArray();
						overlay2.ShowCue(new MemoryMatchMonitor.SubtitleCue(lines[0].Chinese, TimeSpan.FromSeconds(19),
							Commander: true, Sequenced: true, Speaker: "苏联指挥官", Lines: lines));
					}
					else if (preview.Equals(VoicePackRegistry.Soviet, StringComparison.OrdinalIgnoreCase))
					{
						var line = new SovietSubtitlePack(runtimeRoot).Pick("INFANTRY", "DEPLOY");
						overlay2.ShowCue(new MemoryMatchMonitor.SubtitleCue(line.Chinese, TimeSpan.FromSeconds(3.6),
							Commander: false, Speaker: line.Speaker, Original: line.Original));
					}
					else
					{
						JapanVoiceLine line = new JapanVoicePack(runtimeRoot).ById("JP_INF_Y_DEPLOY_01");
						overlay2.ShowCue(new MemoryMatchMonitor.SubtitleCue(line.Chinese, TimeSpan.FromSeconds(3),
							Commander: false, Speaker: "陆军部队", Original: line.Japanese));
					}
					Application.DoEvents();
					using Bitmap bitmap2 = new Bitmap(overlay2.Width, overlay2.Height, PixelFormat.Format32bppArgb);
					overlay2.DrawToBitmap(bitmap2, new Rectangle(Point.Empty, bitmap2.Size));
					if (overlay2.Region is Region windowRegion)
					{
						for (int y = 0; y < bitmap2.Height; y++)
							for (int x = 0; x < bitmap2.Width; x++)
								if (!windowRegion.IsVisible(x, y)) bitmap2.SetPixel(x, y, Color.Transparent);
					}
					string output2 = Path.GetFullPath(subtitleSnapshotPath);
					Directory.CreateDirectory(Path.GetDirectoryName(output2));
					bitmap2.Save(output2, ImageFormat.Png);
					overlay2.HideCue();
					Console.WriteLine("Subtitle snapshot saved: " + output2);
					return 0;
				}
				catch (Exception ex15)
				{
					Console.Error.WriteLine(ex15.ToString());
					return 1;
				}
			}
			if (args != null && args.Length == 2 && args[0] == "--play-jp")
			{
				string id = args[1];
				try
				{
					JapanVoiceLine line2 = new JapanVoicePack(runtimeRoot).ById(id);
					using WaveFileReader wav2 = new WaveFileReader(line2.Path);
					using IntroPlayer player3 = new IntroPlayer();
					Console.WriteLine("Playing " + line2.Id + ": " + line2.Japanese);
					player3.Play(line2.Path);
					Thread.Sleep(wav2.TotalTime + TimeSpan.FromMilliseconds(250.0));
					return 0;
				}
				catch (Exception ex16)
				{
					Console.Error.WriteLine(ex16.Message);
					return 1;
				}
			}
			if (args != null && args.Length == 1 && args[0] == "--play-order-test")
			{
				try
				{
					JapanVoicePack orderPack = new JapanVoicePack(runtimeRoot);
					JapanOrderSubtitle orderSubtitle = orderPack.ResolveOrderSubtitle("card_event_for_prosperety_C");
					using IntroPlayer player4 = new IntroPlayer();
					Console.WriteLine("Playing synthesized telegraph only: " + orderSubtitle.Title + "｜" + orderSubtitle.Line);
					TimeSpan timeout = player4.PlayTelegraph("... --- ...");
					Thread.Sleep(300);
					if (player4.PlaybackState != PlaybackState.Playing)
					{
						throw new InvalidOperationException("Order audio device stopped before the telegraph completed.");
					}
					Console.WriteLine("Order audio device is actively playing PCM telegraph only.");
					Thread.Sleep(timeout);
					return 0;
				}
				catch (Exception ex17)
				{
					Console.Error.WriteLine(ex17.ToString());
					return 1;
				}
			}
			if (args != null && args.Length == 1 && args[0] == "--play-hurt-test")
			{
				try
				{
					JapanVoicePack voices4 = new JapanVoicePack(runtimeRoot);
					JapanVoiceLine line4 = voices4.PickHurt(JapanVoicePack.InfantryPool);
					using IntroPlayer player5 = new IntroPlayer();
					Console.WriteLine("Playing hurt voice " + line4.Id + ": " + line4.Chinese);
					TimeSpan timeout2 = player5.Play(line4.Path);
					Thread.Sleep(200);
					if (player5.PlaybackState != PlaybackState.Playing)
					{
						throw new InvalidOperationException("Hurt voice stopped before playback completed.");
					}
					Console.WriteLine("Hurt voice is playing without a preceding grunt.");
					Thread.Sleep(timeout2);
					return 0;
				}
				catch (Exception ex18)
				{
					Console.Error.WriteLine(ex18.ToString());
					return 1;
				}
			}
			if (args.Length != 0 && (args == null || args.Length != 1 || !(args[0] == "--smoke")))
			{
				Console.Error.WriteLine("Usage: dotnet run [--status|--self-test|--scan-kards-key|--probe-unreal|--find-namepool|--find-kards-objects|--find-kards-fields|--search-unreal-names text|--smoke|--voice-check|--ui-snapshot path [Japan|Soviet|Germany|Finland]|--subtitle-snapshot path [Japan|JapanOrder|Soviet|SovietOrder|SovietIntro|SovietLong|GermanyIntro|GermanyLong|GermanyOrder|FinlandIntro|FinlandLong|FinlandOrder]|--play-jp ID|--play-order-test|--play-hurt-test|--analyze-image path|--replay-frames directory [repeat]|--record-visual seconds]");
				return 1;
			}
			if (args != null && args.Length == 1 && args[0] == "--smoke")
			{
				try
				{
					JapanVoicePack voices5 = new JapanVoicePack(runtimeRoot);
					using IntroPlayer player6 = new IntroPlayer();
					using MemoryMatchMonitor monitor3 = new MemoryMatchMonitor(dataRoot, player6, voices5);
					using InputDesktopHotkey hotkey = new InputDesktopHotkey(monitor3.Hotkey);
					hotkey.Start();
					monitor3.Start();
					Console.WriteLine($"KardsVox started with {voices5.Count} Japan voice clips.");
					Thread.Sleep(2000);
					hotkey.Stop();
					hotkey.WaitForExit();
					Console.WriteLine("Startup smoke test passed.");
					return 0;
				}
				catch (Exception ex19)
				{
					Console.Error.WriteLine("KardsVox could not start: " + ex19.Message);
					return 1;
				}
			}
			try
			{
				bool firstInstance;
				using (new Mutex(initiallyOwned: true, "Local\\KardsVox-Japan-Voice-Module", out firstInstance))
				{
					if (!firstInstance)
					{
						MessageBox.Show("KARDS VOX 已经在运行。", "KARDS VOX", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
						return 0;
					}
					ApplicationConfiguration.Initialize();
					JapanVoicePack voices5 = new JapanVoicePack(runtimeRoot);
					using IntroPlayer player6 = new IntroPlayer();
					MemoryMatchMonitor monitor4 = new MemoryMatchMonitor(dataRoot, player6, voices5);
					try
					{
						InputDesktopHotkey hotkey2 = new InputDesktopHotkey(monitor4.Hotkey);
						try
						{
							SubtitleOverlay overlay3 = new SubtitleOverlay();
							try
							{
								TrainerForm form2 = new TrainerForm(monitor4, overlay3);
								try
								{
									Action<MemoryMatchMonitor.SubtitleCue> showSubtitle = overlay3.ShowCue;
									Action clearSubtitle = overlay3.HideCue;
									Action<TimeSpan> transitionSubtitle = overlay3.BeginTransition;
									monitor4.SubtitleRequested += showSubtitle;
									monitor4.SubtitleCleared += clearSubtitle;
									monitor4.SubtitleTransitionRequested += transitionSubtitle;
									Task startup = null;
									form2.Shown += delegate
									{
										_ = overlay3.Handle;
										startup = Task.Run(delegate
										{
											try
											{
												hotkey2.Start();
												monitor4.Start();
											}
											catch (Exception ex20)
											{
												Exception ex21 = ex20;
												Exception ex22 = ex21;
												if (!form2.IsDisposed)
												{
													form2.BeginInvoke(delegate
													{
														MessageBox.Show(form2, "连接 KARDS 失败：\n" + ex22.Message, "KARDS VOX", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
													});
												}
											}
										});
									};
									Application.Run(form2);
									monitor4.SubtitleRequested -= showSubtitle;
									monitor4.SubtitleCleared -= clearSubtitle;
									monitor4.SubtitleTransitionRequested -= transitionSubtitle;
									if (startup != null)
									{
										startup.GetAwaiter().GetResult();
									}
									hotkey2.Stop();
									return 0;
								}
								finally
								{
									if (form2 != null)
									{
										((IDisposable)form2).Dispose();
									}
								}
							}
							finally
							{
								if (overlay3 != null)
								{
									((IDisposable)overlay3).Dispose();
								}
							}
						}
						finally
						{
							if (hotkey2 != null)
							{
								((IDisposable)hotkey2).Dispose();
							}
						}
					}
					finally
					{
						if (monitor4 != null)
						{
							((IDisposable)monitor4).Dispose();
						}
					}
				}
			}
			catch (Exception ex19)
			{
				MessageBox.Show("KARDS VOX 无法启动：\n" + ex19.Message, "KARDS VOX", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				return 1;
			}
		}
		static string FindDataRoot(string text2)
		{
			if (File.Exists(Path.Combine(text2, "Data", "Japan", "voice_lines.tsv")))
			{
				return text2;
			}
			DirectoryInfo directory = new DirectoryInfo(text2);
			int i = 0;
			while (directory != null && i < 6)
			{
				if (directory.Name.Equals("KardsVox.build", StringComparison.OrdinalIgnoreCase))
				{
					return directory.FullName;
				}
				i++;
				directory = directory.Parent;
			}
			return text2;
		}
	}
}
