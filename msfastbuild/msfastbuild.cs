// msfastbuild.cs - Generates and executes a bff file for fastbuild from a .sln or .vcxproj.
// Copyright 2016 Liam Flookes and Yassine Riahi
// Available under an MIT license. See license file on github for details.
using System;
using System.Collections.Generic;
using System.Linq;
using CommandLine;
using CommandLine.Text;
using System.IO;
using System.Reflection;
using System.Text;

using Microsoft.Build.Evaluation;
using Microsoft.Build.Construction;
using Microsoft.Build.Utilities;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace msfastbuild
{
	public class Options
	{
		[Option('p', "vcproject", DefaultValue = "",
		HelpText = "Path of .vcxproj file to build, or project name if a solution is provided.")]
		public string Project { get; set; }

		[Option('s', "sln", DefaultValue = "",
		HelpText = "Path of .sln file which contains the projects.")]
		public string Solution { get; set; }

		[Option('c', "config", DefaultValue = "",
		HelpText = "Config file to build.")]
		public string Config { get; set; }

		[Option('f', "platform", DefaultValue = "x64",
		HelpText = "Platform to build: win32, x64")]
		public string Platform { get; set; }

		[Option('v', "vs", DefaultValue = "vs2019",		//指定
		HelpText = "visual studio version: vs2017, vs2019")]
		public string VisualStudioVersion { get; set; }

		[Option('a', "fbargs", DefaultValue = "-dist -ide -clean",
		HelpText = "Arguments that pass through to FASTBuild.")]
		public string FBArgs { get; set; }

		[Option('g', "generateonly", DefaultValue = false,
		HelpText = "Generate bff file only, without calling FASTBuild.")]
		public bool GenerateOnly { get; set; }

		[Option('r', "regen", DefaultValue = false,
		HelpText = "Regenerate bff file even when the project hasn't changed.")]
		public bool AlwaysRegenerate { get; set; }

		[Option('b', "fbpath", DefaultValue = "",
		HelpText = "Path to FASTBuild executable.")]
		public string FBPath { get; set; }

		[Option('u', "unity", DefaultValue = false,
		HelpText = "Whether to combine files into a unity step. May substantially improve compilation time, but not all projects are suitable.")]
		public bool UseUnity { get; set; }

		[OptionList('t', "thirdparty", Separator = '|', DefaultValue = new string[] { }, 
			HelpText = "Additional thirdparty files (usually dlls) required by the compiler. Not ends with slash." )]
		public IList<string> ThirdParty { get; set; }
		 

		[HelpOption]
		public string GetUsage()
		{
			return HelpText.AutoBuild(this,(HelpText current) => HelpText.DefaultParsingErrorsHandler(this, current));
		}
	}

	/// <summary>
	/// Available compiler toolchains on Windows platform
	/// </summary>
	public enum WindowsCompiler
	{
		/// <summary>
		/// Use the default compiler. A specific value will always be used outside of configuration classes.
		/// </summary>
		Default,

		/// <summary>
		/// Use Clang for Windows, using the clang-cl driver.
		/// </summary>
		Clang,

		/// <summary>
		/// Use the Intel C++ compiler
		/// </summary>
		Intel,

		/// <summary>
		/// Visual Studio 2015 (Visual C++ 14.0)
		/// </summary>
		VisualStudio2015,
		
		/// <summary>
		/// Visual Studio 2017 (Visual C++ 15.0)
		/// </summary>
		VisualStudio2017,

		/// <summary>
		/// Visual Studio 2019 (Visual C++ 16.0)
		/// </summary>
		VisualStudio2019,
	}

	public class msfastbuild
	{
		static public string PlatformToolsetVersion = "140";    //vs2015, vs2017, vs2019 都是140
		static WindowsCompiler Compiler = WindowsCompiler.Default;

		static public string VCBasePath = "";
		static public string VCExePath = "";
		static public string BFFOutputFilePath = "fbuild.bff";
		static public Options CommandLineOptions = new Options();
        static public string WindowsSDKTarget = ""; //10.0.18362.0
		static public MSFBProject CurrentProject;
		static public Assembly CPPTasksAssembly;
		static public string PreBuildBatchFile = "";
		static public string PostBuildBatchFile = "";
		static public string SolutionDir = "";
		static public bool HasCompileActions = true;

	public enum BuildType
		{
		    Application,
		    StaticLib,
		    DynamicLib
		}

		static public BuildType BuildOutput = BuildType.Application;

		public class MSFBProject
		{
			public Project Proj;
			public List<MSFBProject> Dependents = new List<MSFBProject>();
			public string AdditionalLinkInputs = "";
		}


		static void Main(string[] args)
		{
			Parser parser = new Parser();
            if (!parser.ParseArguments(args, CommandLineOptions))
            {
                Console.WriteLine(CommandLineOptions.GetUsage());
                return;
            }
            if (string.IsNullOrEmpty(CommandLineOptions.Solution) && string.IsNullOrEmpty(CommandLineOptions.Project))
			{
				Console.WriteLine("No solution or project provided!");
				Console.WriteLine(CommandLineOptions.GetUsage());
				return;
			}

			if (CommandLineOptions.VisualStudioVersion == "vs2019")
			{
				Compiler = WindowsCompiler.VisualStudio2019;
			}
			else if (CommandLineOptions.VisualStudioVersion == "vs2017")
			{
				Compiler = WindowsCompiler.VisualStudio2017;
			}
			else
			{
				Console.WriteLine("Unsupport Complier!");
				Console.WriteLine(CommandLineOptions.GetUsage());
				return;
			}


			List <string> ProjectsToBuild = new List<string>();
			if (!string.IsNullOrEmpty(CommandLineOptions.Solution) && File.Exists(CommandLineOptions.Solution))
			{
				try
				{
					if (string.IsNullOrEmpty(CommandLineOptions.Project))
					{
						List<ProjectInSolution> SolutionProjects = SolutionFile.Parse(Path.GetFullPath(CommandLineOptions.Solution)).ProjectsInOrder.Where(el => el.ProjectType == SolutionProjectType.KnownToBeMSBuildFormat).ToList();
						SolutionProjects.Sort((x, y) => //Very dubious sort.
						{
							if (x.Dependencies.Contains(y.ProjectGuid)) return 1;
							if (y.Dependencies.Contains(x.ProjectGuid)) return -1;
							return 0;
						});
						ProjectsToBuild = SolutionProjects.ConvertAll(el => el.AbsolutePath);
					}
					else
					{
						ProjectsToBuild.Add(Path.GetFullPath(CommandLineOptions.Project));
					}

					SolutionDir = Path.GetDirectoryName(Path.GetFullPath(CommandLineOptions.Solution));
					SolutionDir = SolutionDir.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
					if(SolutionDir.Last() != Path.AltDirectorySeparatorChar)
						SolutionDir += Path.AltDirectorySeparatorChar;
				}
				catch (Exception e)
				{
					Console.WriteLine("Failed to parse solution file " + CommandLineOptions.Solution + "!");
					Console.WriteLine("Exception: " + e.ToString());
					return;
				}
			}
			else if (!string.IsNullOrEmpty(CommandLineOptions.Project))
			{
				ProjectsToBuild.Add(Path.GetFullPath(CommandLineOptions.Project));
			}

			List<MSFBProject> EvaluatedProjects = new List<MSFBProject>();

			for (int i=0; i < ProjectsToBuild.Count; ++i)
			{
				EvaluateProjectReferences(ProjectsToBuild[i], EvaluatedProjects, null);
			}

			int ProjectsBuilt = 0;
			foreach(MSFBProject project in EvaluatedProjects)
			{
				CurrentProject = project;

				string VCTargetsPath = CurrentProject.Proj.GetPropertyValue("VCTargetsPathEffective");
				if (string.IsNullOrEmpty(VCTargetsPath))
				{
					VCTargetsPath = CurrentProject.Proj.GetPropertyValue("VCTargetsPath");
				}
				if (string.IsNullOrEmpty(VCTargetsPath))
				{
					Console.WriteLine("Failed to evaluate VCTargetsPath variable on " + Path.GetFileName(CurrentProject.Proj.FullPath) + "!");
					continue;
				}

				bool useBuiltinDll = true;
				string BuildDllName = "Microsoft.Build.CPPTasks.Common.dll";
				string BuildDllPath = VCTargetsPath + BuildDllName;
				if (File.Exists(BuildDllPath))
				{
					CPPTasksAssembly = Assembly.LoadFrom(BuildDllPath);
					if (CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.CL") != null &&
						CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.RC") != null &&
						CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.Link") != null &&
						CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.LIB") != null)
					{
						useBuiltinDll = false;
					}
				}
				if (useBuiltinDll)
				{
					CPPTasksAssembly = Assembly.LoadFrom(AppDomain.CurrentDomain.BaseDirectory + BuildDllName);
				}

				BFFOutputFilePath = Path.GetDirectoryName(CurrentProject.Proj.FullPath) + "\\" + Path.GetFileName(CurrentProject.Proj.FullPath) + "_" + CommandLineOptions.Config.Replace(" ", "") + "_" + CommandLineOptions.Platform.Replace(" ", "") + ".bff";
				GenerateBffFromVcxproj(CommandLineOptions.Config, CommandLineOptions.Platform);
				Console.WriteLine("BFF: {0}\r\n", BFFOutputFilePath);
				if (!CommandLineOptions.GenerateOnly)
				{
					if (HasCompileActions && !ExecuteBffFile(CurrentProject.Proj.FullPath))
						break;
					else
						ProjectsBuilt++;
				}
			}

			Console.WriteLine(ProjectsBuilt + "/" + EvaluatedProjects.Count + " built.");
		}

		static public void EvaluateProjectReferences(string ProjectPath, List<MSFBProject> evaluatedProjects, MSFBProject dependent)
		{
			if (!string.IsNullOrEmpty(ProjectPath) && File.Exists(ProjectPath))
			{
				try
				{
					MSFBProject newProj = evaluatedProjects.Find(elem => elem.Proj.FullPath == Path.GetFullPath(ProjectPath));
					if (newProj != null)
					{
						//Console.WriteLine("Found exisiting project " + Path.GetFileNameWithoutExtension(ProjectPath));
						if (dependent != null)
							newProj.Dependents.Add(dependent);
					}
					else
					{
						ProjectCollection projColl = new ProjectCollection();
						if (!string.IsNullOrEmpty(SolutionDir))
							projColl.SetGlobalProperty("SolutionDir", SolutionDir);
						newProj = new MSFBProject();
						Project proj = projColl.LoadProject(ProjectPath);

						if (proj != null)
						{
							proj.SetGlobalProperty("Configuration", CommandLineOptions.Config);
							proj.SetGlobalProperty("Platform", CommandLineOptions.Platform);
							if (!string.IsNullOrEmpty(SolutionDir))
								proj.SetGlobalProperty("SolutionDir", SolutionDir);
							proj.ReevaluateIfNecessary();

							newProj.Proj = proj;
							if (dependent != null)
							{
								newProj.Dependents.Add(dependent);
							}
							var ProjectReferences = proj.Items.Where(elem => elem.ItemType == "ProjectReference");
							foreach (var ProjRef in ProjectReferences)
							{
								if (ProjRef.GetMetadataValue("ReferenceOutputAssembly") == "true" || ProjRef.GetMetadataValue("LinkLibraryDependencies") == "true")
								{
									//Console.WriteLine(string.Format("{0} referenced by {1}.", Path.GetFileNameWithoutExtension(ProjRef.EvaluatedInclude), Path.GetFileNameWithoutExtension(proj.FullPath)));
									EvaluateProjectReferences(Path.GetDirectoryName(proj.FullPath) + Path.DirectorySeparatorChar + ProjRef.EvaluatedInclude, evaluatedProjects, newProj);
								}
							}
							//Console.WriteLine("Adding " + Path.GetFileNameWithoutExtension(proj.FullPath));
							evaluatedProjects.Add(newProj);
						}
					}
				}
				catch (Exception e)
				{
					Console.WriteLine("Failed to parse project file " + ProjectPath + "!");
					Console.WriteLine("Exception: " + e.Message);
					return;
				}
			}
		}

		static public bool HasFileChanged(string InputFile, string Platform, string Config, out string MD5hash)
		{
			using (var md5 = System.Security.Cryptography.MD5.Create())
			{
				using (var stream = File.OpenRead(InputFile))
				{
				    MD5hash = ";" + InputFile + "_" + Platform + "_" + Config + "_" + BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", "").ToLower();
				}
			}
			
			if (!File.Exists(BFFOutputFilePath))
				return true;
			
			string FirstLine = File.ReadAllLines(BFFOutputFilePath).First(); //bit wasteful to read the whole file...
			if (FirstLine == MD5hash) 
				return false;
			else
				return true;
		}

		static bool ExecuteBffFile(string ProjectPath)
		{
			string projectDir = Path.GetDirectoryName(ProjectPath) + "\\";


            //Interesting flags for FASTBuild: -nostoponerror, -verbose, -monitor (if FASTBuild Monitor Visual Studio Extension is installed!)
            // Yassine: The -clean is to bypass the FastBuild internal dependencies checks (cached in the fdb) as it could create some conflicts with UBT.
            //			Basically we want FB to stupidly compile what UBT tells it to.
            //string FBCommandLine = string.Format("-monitor -summary -dist -clean -ide -config \"{0}\"", BFFOutputFilePath);
            string FBCommandLine = string.Format("-summary {0} -config \"{1}\"", CommandLineOptions.FBArgs, BFFOutputFilePath);

            ProcessStartInfo FBStartInfo = new ProcessStartInfo(string.IsNullOrEmpty(CommandLineOptions.FBPath) ? "fbuild.exe" : CommandLineOptions.FBPath, FBCommandLine);

			FBStartInfo.UseShellExecute = false;
			FBStartInfo.WorkingDirectory = projectDir;
			Console.WriteLine("Building " + Path.GetFileNameWithoutExtension(ProjectPath));

			try
			{
				Process FBProcess = new Process();
				FBProcess.StartInfo = FBStartInfo;

				FBStartInfo.RedirectStandardError = true;
				FBStartInfo.RedirectStandardOutput = true;
				FBProcess.EnableRaisingEvents = true;

				DataReceivedEventHandler OutputEventHandler = (Sender, Args) =>
				{
					if (Args.Data != null)
						Console.WriteLine(Args.Data);
				};

				FBProcess.OutputDataReceived += OutputEventHandler;
				FBProcess.ErrorDataReceived += OutputEventHandler;
				Console.WriteLine("RUN: {0} {1}", FBStartInfo.FileName, FBStartInfo.Arguments);
				FBProcess.Start();

				FBProcess.BeginOutputReadLine();
				FBProcess.BeginErrorReadLine();

				FBProcess.WaitForExit();
				return FBProcess.ExitCode == 0;
			}
			catch (Exception e)
			{
				Console.WriteLine("Exception launching fbuild process. Is it in your path?" + e.ToString());
				return false;
			}
		}

		public class ObjectListNode
		{
			string Compiler;
			string CompilerOutputPath;
			string CompilerOptions;
			string CompilerOutputExtension;
			string PrecompiledHeaderString;

			List<string> CompilerInputFiles;
		
			public ObjectListNode(string InputFile, string InCompiler, string InCompilerOutputPath, string InCompilerOptions, string InPrecompiledHeaderString, string InCompilerOutputExtension = "")
			{
				CompilerInputFiles = new List<string>();
				CompilerInputFiles.Add(InputFile);
				Compiler = InCompiler;
				CompilerOutputPath = InCompilerOutputPath;
				CompilerOptions = InCompilerOptions;
				CompilerOutputExtension = InCompilerOutputExtension;
				PrecompiledHeaderString = InPrecompiledHeaderString;
			}
		
			public bool AddIfMatches(string InputFile, string InCompiler, string InCompilerOutputPath, string InCompilerOptions, string InPrecompiledHeaderString)
			{
				if(Compiler == InCompiler && CompilerOutputPath == InCompilerOutputPath && CompilerOptions == InCompilerOptions && PrecompiledHeaderString == InPrecompiledHeaderString)
				{
					CompilerInputFiles.Add(InputFile);
					return true;
				}
				return false;
			}
		
			public string ToString(int ActionNumber)
			{
				bool UsedUnity = false;
				string ResultString = "";
				if(CommandLineOptions.UseUnity && Compiler != "rc" && CompilerInputFiles.Count > 1)
				{
					StringBuilder UnityListString = new StringBuilder(string.Format("Unity('unity_{0}')\n{{\n", ActionNumber));
					UnityListString.AppendFormat("\t.UnityInputFiles = {{ {0} }}\n", string.Join(",", CompilerInputFiles.ConvertAll(el => string.Format("'{0}'", el)).ToArray()));
					UnityListString.AppendFormat("\t.UnityOutputPath = \"{0}\"\n", CompilerOutputPath);
					UnityListString.AppendFormat("\t.UnityNumFiles = {0}\n", 1 + CompilerInputFiles.Count/10);
					UnityListString.Append("}\n\n");
					UsedUnity = true;
					ResultString = UnityListString.ToString();
				}

				StringBuilder ObjectListString = new StringBuilder(string.Format("ObjectList('action_{0}')\n{{\n", ActionNumber));
				ObjectListString.AppendFormat("\t.Compiler = '{0}'\n", Compiler);
				ObjectListString.AppendFormat("\t.CompilerOutputPath = \"{0}\"\n", CompilerOutputPath);
				if(UsedUnity)
				{
					ObjectListString.AppendFormat("\t.CompilerInputUnity = {{ {0} }}\n", string.Format("'unity_{0}'", ActionNumber));
				}
				else
				{
					ObjectListString.AppendFormat("\t.CompilerInputFiles = {{ {0} }}\n", string.Join(",", CompilerInputFiles.ConvertAll(el => string.Format("'{0}'", el)).ToArray()));
				}				
				ObjectListString.AppendFormat("\t.CompilerOptions = '{0}'\n", CompilerOptions);
				if (!string.IsNullOrEmpty(CompilerOutputExtension))
				{
					ObjectListString.AppendFormat("\t.CompilerOutputExtension = '{0}'\n", CompilerOutputExtension);
				}
				if (!string.IsNullOrEmpty(PrecompiledHeaderString))
				{
					ObjectListString.Append(PrecompiledHeaderString);
				}
				if (!string.IsNullOrEmpty(PreBuildBatchFile))
				{
					ObjectListString.Append("\t.PreBuildDependencies  = 'prebuild'\n");
				}
				ObjectListString.Append("}\n\n");
				ResultString += ObjectListString.ToString();
				return ResultString;
			}
		}

		static private void AddExtraDlls(StringBuilder outputString, string rootDir, string pattern)
		{
			string[] dllFiles = Directory.GetFiles(rootDir, pattern);
			foreach (string dllFile in dllFiles)
			{
				outputString.AppendFormat("\t\t'$Root$\\{0}'\n", Path.GetFileName(dllFile));
			}
		}

		static private void AddExtraFiles(StringBuilder outputString, string rootDir)
		{
			var numericDirectories = Directory.GetDirectories(rootDir);
			if (numericDirectories.Any())
			{
				foreach (var dir in numericDirectories)
				{
					AddExtraFiles(outputString, dir);	//递归
				}
			}
			string[] files = Directory.GetFiles(rootDir);
			foreach (string aFile in files)
			{
				outputString.AppendFormat("\t\t'{0}'\n", aFile);
			}
		}
		static private void GenerateBffFromVcxproj(string Config, string Platform)
		{
			Project ActiveProject = CurrentProject.Proj;
			string MD5hash = "wafflepalooza";
			PreBuildBatchFile = "";
			PostBuildBatchFile = "";
			bool FileChanged = HasFileChanged(ActiveProject.FullPath, Platform, Config, out MD5hash);

			string configType = ActiveProject.GetProperty("ConfigurationType").EvaluatedValue;
			switch(configType)
			{
				case "DynamicLibrary": BuildOutput = BuildType.DynamicLib; break;
				case "StaticLibrary": BuildOutput = BuildType.StaticLib; break;
				default:
				case "Application": BuildOutput = BuildType.Application; break;				
			}

			//PlatformToolsetVersion = ActiveProject.GetProperty("PlatformToolsetVersion").EvaluatedValue;

			string OutDir = ActiveProject.GetProperty("OutDir").EvaluatedValue;
			string IntDir = ActiveProject.GetProperty("IntDir").EvaluatedValue;

			StringBuilder OutputString = new StringBuilder(MD5hash + "\n\n");

			OutputString.AppendFormat(".VSBasePath = '{0}'\n", ActiveProject.GetProperty("VSInstallDir").EvaluatedValue);
			VCBasePath = ActiveProject.GetProperty("VCInstallDir").EvaluatedValue;
			OutputString.AppendFormat(".VCBasePath = '{0}'\n", VCBasePath);

			if (Platform == "Win32" || Platform == "x86")
			{
				VCExePath = ActiveProject.GetProperty("VC_ExecutablePath_x86_x86").EvaluatedValue;
			}
			else
			{
				VCExePath = ActiveProject.GetProperty("VC_ExecutablePath_x64_x64").EvaluatedValue;
			}
			OutputString.AppendFormat(".VCExePath = '{0}'\n", VCExePath );

			WindowsSDKTarget = ActiveProject.GetProperty("WindowsTargetPlatformVersion") != null ? ActiveProject.GetProperty("WindowsTargetPlatformVersion").EvaluatedValue : "8.1";

			string winSdkDir = ActiveProject.GetProperty("WindowsSdkDir").EvaluatedValue;
			OutputString.AppendFormat(".WindowsSDKBasePath = '{0}'\n\n", winSdkDir);

			OutputString.Append("Settings\n{\n\t.Environment = \n\t{\n");
			OutputString.AppendFormat("\t\t\"INCLUDE={0}\",\n", ActiveProject.GetProperty("IncludePath").EvaluatedValue);
			OutputString.AppendFormat("\t\t\"LIB={0}\",\n", ActiveProject.GetProperty("LibraryPath").EvaluatedValue);
			OutputString.AppendFormat("\t\t\"LIBPATH={0}\",\n", ActiveProject.GetProperty("ReferencePath").EvaluatedValue);
			OutputString.AppendFormat("\t\t\"PATH={0}\"\n", ActiveProject.GetProperty("Path").EvaluatedValue);
			OutputString.AppendFormat("\t\t\"TMP={0}\"\n", ActiveProject.GetProperty("Temp").EvaluatedValue);
			OutputString.AppendFormat("\t\t\"TEMP={0}\"\n", ActiveProject.GetProperty("Temp").EvaluatedValue);
			OutputString.AppendFormat("\t\t\"SystemRoot={0}\"\n", ActiveProject.GetProperty("SystemRoot").EvaluatedValue);
			OutputString.Append("\t}\n}\n\n");

			StringBuilder CompilerString = new StringBuilder("Compiler('msvc')\n{\n");

			string CompilerRoot = VCExePath;
			CompilerString.Append("\t.Root = '$VCExePath$'\n");
			CompilerString.Append("\t.Executable = '$Root$\\cl.exe'\n");
			CompilerString.Append("\t.ExtraFiles =\n\t{\n");
			CompilerString.Append("\t\t'$Root$\\c1.dll'\n");
			CompilerString.Append("\t\t'$Root$\\c1xx.dll'\n");
			CompilerString.Append("\t\t'$Root$\\c2.dll'\n");
			CompilerString.Append("\t\t'$Root$\\atlprov.dll'\n");// Only needed if using ATL


			if (File.Exists(CompilerRoot + "1033\\clui.dll")) //Check English first...
			{
				CompilerString.Append("\t\t'$Root$\\1033\\clui.dll'\n");
			}
			else
			{
				var numericDirectories = Directory.GetDirectories(CompilerRoot).Where(d => Path.GetFileName(d).All(char.IsDigit));
				var cluiDirectories = numericDirectories.Where(d => Directory.GetFiles(d, "clui.dll").Any());
				if(cluiDirectories.Any())
				{
					CompilerString.AppendFormat("\t\t'$Root$\\{0}\\clui.dll'\n", Path.GetFileName(cluiDirectories.First()));
					CompilerString.AppendFormat(string.Format("\t\t'$Root$\\{0}\\mspft{1}ui.dll'\n", Path.GetFileName(cluiDirectories.First()), PlatformToolsetVersion));  // Localized messages for static analysis https://www.fastbuild.org/docs/functions/compiler.html

				}
			}
			
			CompilerString.Append("\t\t'$Root$\\mspdbsrv.exe'\n");
			CompilerString.Append("\t\t'$Root$\\mspdbcore.dll'\n");

			CompilerString.AppendFormat("\t\t'$Root$\\mspft{0}.dll'\n", PlatformToolsetVersion);
			CompilerString.AppendFormat("\t\t'$Root$\\msobj{0}.dll'\n", PlatformToolsetVersion);
			CompilerString.AppendFormat("\t\t'$Root$\\mspdb{0}.dll'\n", PlatformToolsetVersion);

			var redistDirs = Directory.GetDirectories(VCBasePath.ToString() + "Redist\\MSVC\\", "*", SearchOption.TopDirectoryOnly);

			if (redistDirs.Length > 0)
			{
				Regex regex = new Regex(@"\d{2}\.\d{2}\.\d{5}$");
				string redistDir = redistDirs.First((s) =>
				{
					return regex.IsMatch(s);
				});
				if (Compiler == WindowsCompiler.VisualStudio2019)
				{
					//CompilerString.AppendFormat("\t\t'{0}\\x64\\Microsoft.VC142.CRT\\msvcp{1}.dll'\n", redistDir, PlatformToolsetVersion);
					//CompilerString.AppendFormat("\t\t'{0}\\x64\\Microsoft.VC142.CRT\\vccorlib{1}.dll'\n", redistDir, PlatformToolsetVersion);
					//CompilerString.AppendFormat("\t\t'{0}\\x64\\Microsoft.VC142.CRT\\vcruntime{1}.dll'\n", redistDir, PlatformToolsetVersion);
					//CompilerString.AppendFormat("\t\t'{0}\\x64\\Microsoft.VC142.CRT\\vcruntime{1}_1.dll'\n", redistDir, PlatformToolsetVersion); // Required as of 16.5.1 (14.25.28610)
				}
				else if (Compiler  == WindowsCompiler.VisualStudio2017)
				{
					//VS 2017 is really confusing in terms of version numbers and paths so these values might need to be modified depending on what version of the tool chain you
					// chose to install.
					CompilerString.AppendFormat("\t\t'{0}\\x64\\Microsoft.VC141.CRT\\msvcp{1}.dll'\n", redistDir, PlatformToolsetVersion);
					CompilerString.AppendFormat("\t\t'{0}\\x64\\Microsoft.VC141.CRT\\vccorlib{1}.dll'\n", redistDir, PlatformToolsetVersion);
				}
			}
			CompilerString.AppendFormat("\t\t'$Root$\\tbbmalloc.dll'\n");

			//导入依赖目录
			foreach (string path in CommandLineOptions.ThirdParty)
			{
				string dir = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);  //统一到windows的反斜杠 \
				if (dir.Last() != Path.DirectorySeparatorChar)
					AddExtraFiles(CompilerString, path);
				else
					AddExtraFiles(CompilerString, Path.GetDirectoryName(dir));
			}
			//foreach (string path in FASTBuild_Toolchain)
			//{
			//	AddExtraFiles(CompilerString, path);
			//}			//
			
			CompilerString.Append("\t}\n"); //End extra files
			CompilerString.Append("}\n\n"); //End compiler

			string rcPath = "\\bin\\" + WindowsSDKTarget + "\\x64\\rc.exe";
			if (!File.Exists(winSdkDir + rcPath))
			{
				rcPath = "\\bin\\x64\\rc.exe";
			}

			CompilerString.Append("Compiler('rc')\n{\n");
			CompilerString.Append("\t.Executable = '$WindowsSDKBasePath$" + rcPath + "'\n");
			CompilerString.Append("\t.CompilerFamily = 'custom'\n");
			CompilerString.Append("}\n\n"); //End rc compiler

			OutputString.Append(CompilerString);

			if (ActiveProject.GetItems("PreBuildEvent").Any())
			{
				var buildEvent = ActiveProject.GetItems("PreBuildEvent").First();
				if (buildEvent.Metadata.Any())
				{
					var mdPi = buildEvent.Metadata.First();
					if(!string.IsNullOrEmpty(mdPi.EvaluatedValue))
					{
						string BatchText = "call \"" + VCBasePath + "Auxiliary\\Build\\vcvarsall.bat\" "
							+ (Platform == "Win32" ? "x86" : "x64") + " " + WindowsSDKTarget + "\n";
						PreBuildBatchFile = Path.Combine(ActiveProject.DirectoryPath, Path.GetFileNameWithoutExtension(ActiveProject.FullPath) + "_prebuild.bat");
						File.WriteAllText(PreBuildBatchFile, BatchText + mdPi.EvaluatedValue);						
						OutputString.Append("Exec('prebuild') \n{\n");
						OutputString.AppendFormat("\t.ExecExecutable = '{0}' \n", PreBuildBatchFile);
						OutputString.AppendFormat("\t.ExecInput = '{0}' \n", PreBuildBatchFile);
						OutputString.AppendFormat("\t.ExecOutput = '{0}' \n", PreBuildBatchFile + ".txt");
						OutputString.Append("\t.ExecUseStdOutAsOutput = true \n");
						OutputString.Append("}\n\n");
					}
				}
			}

			string CompilerOptions = "";

			List<ObjectListNode> ObjectLists = new List<ObjectListNode>();
			var CompileItems = ActiveProject.GetItems("ClCompile");
			string PrecompiledHeaderString = "";

			foreach (var Item in CompileItems)
			{
				if (Item.DirectMetadata.Any())
				{
					if (Item.DirectMetadata.Where(dmd => dmd.Name == "ExcludedFromBuild" && dmd.EvaluatedValue == "true").Any())
						continue;
					if (Item.DirectMetadata.Where(dmd => dmd.Name == "PrecompiledHeader" && dmd.EvaluatedValue == "Create").Any())
					{
						ToolTask CLtask = (ToolTask)Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.CL"));
						CLtask.GetType().GetProperty("Sources").SetValue(CLtask, new TaskItem[] { new TaskItem() });
						string pchCompilerOptions = GenerateTaskCommandLine(CLtask, new string[] { "PrecompiledHeaderOutputFile", "ObjectFileName", "AssemblerListingLocation" }, Item.Metadata) + " /FS";
						PrecompiledHeaderString = "\t.PCHOptions = '" + string.Format("\"%1\" /Fp\"%2\" /Fo\"%3\" {0} '\n", pchCompilerOptions);
						PrecompiledHeaderString += "\t.PCHInputFile = '" + Item.EvaluatedInclude + "'\n";
						PrecompiledHeaderString += "\t.PCHOutputFile = '" + Item.GetMetadataValue("PrecompiledHeaderOutputFile") + "'\n";
						break; //Assumes only one pch...
					}
				}
			}

			foreach (var Item in CompileItems)
			{
				bool ExcludePrecompiledHeader = false;
				if (Item.DirectMetadata.Any())
				{
					if (Item.DirectMetadata.Where(dmd => dmd.Name == "ExcludedFromBuild" && dmd.EvaluatedValue == "true").Any())
						continue;
					if (Item.DirectMetadata.Where(dmd => dmd.Name == "PrecompiledHeader" && dmd.EvaluatedValue == "Create").Any())
						continue;
					if (Item.DirectMetadata.Where(dmd => dmd.Name == "PrecompiledHeader" && dmd.EvaluatedValue == "NotUsing").Any())
						ExcludePrecompiledHeader = true;
				}

				ToolTask Task = (ToolTask) Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.CL"));
				Task.GetType().GetProperty("Sources").SetValue(Task, new TaskItem[] { new TaskItem() }); //CPPTasks throws an exception otherwise...
				string TempCompilerOptions = GenerateTaskCommandLine(Task, new string[] { "ObjectFileName", "AssemblerListingLocation" }, Item.Metadata) + " /FS";
				if (Path.GetExtension(Item.EvaluatedInclude) == ".c")
					TempCompilerOptions += " /TC";
				else
					TempCompilerOptions += " /TP";
				CompilerOptions = TempCompilerOptions;
				string FormattedCompilerOptions = string.Format("\"%1\" /Fo\"%2\" {0}", TempCompilerOptions);
				var MatchingNodes = ObjectLists.Where(el => el.AddIfMatches(Item.EvaluatedInclude, "msvc", IntDir, FormattedCompilerOptions, ExcludePrecompiledHeader ? "" : PrecompiledHeaderString));
				if(!MatchingNodes.Any())
				{
					ObjectLists.Add(new ObjectListNode(Item.EvaluatedInclude, "msvc", IntDir, FormattedCompilerOptions, ExcludePrecompiledHeader ? "" : PrecompiledHeaderString));
				}
			}

			PrecompiledHeaderString = "";

			var ResourceCompileItems = ActiveProject.GetItems("ResourceCompile");
			foreach (var Item in ResourceCompileItems)
			{
				if (Item.DirectMetadata.Any())
				{
					if (Item.DirectMetadata.Where(dmd => dmd.Name == "ExcludedFromBuild" && dmd.EvaluatedValue == "true").Any())
						continue;
				}
			
				ToolTask Task = (ToolTask)Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.RC"));
				string ResourceCompilerOptions = GenerateTaskCommandLine(Task, new string[] { "ResourceOutputFileName", "DesigntimePreprocessorDefinitions" }, Item.Metadata);
			
				string formattedCompilerOptions = string.Format("{0} /fo\"%2\" \"%1\"", ResourceCompilerOptions);
				var MatchingNodes = ObjectLists.Where(el => el.AddIfMatches(Item.EvaluatedInclude, "rc", IntDir, formattedCompilerOptions, PrecompiledHeaderString));
				if (!MatchingNodes.Any())
				{
					ObjectLists.Add(new ObjectListNode(Item.EvaluatedInclude, "rc", IntDir, formattedCompilerOptions, PrecompiledHeaderString, ".res"));
				}
			}

			int ActionNumber = 0;
			foreach (ObjectListNode ObjList in ObjectLists)
			{
				OutputString.Append(ObjList.ToString(ActionNumber));
				ActionNumber++;		
			}

			if (ActionNumber > 0)
			{
				HasCompileActions = true;
			}
			else
			{
				HasCompileActions = false;
				Console.WriteLine("Project has no actions to compile.");
			}

			string CompileActions = string.Join(",", Enumerable.Range(0, ActionNumber).ToList().ConvertAll(x => string.Format("'action_{0}'", x)).ToArray());

			if (BuildOutput == BuildType.Application || BuildOutput == BuildType.DynamicLib)
			{
				OutputString.AppendFormat("{0}('output')\n{{", BuildOutput == BuildType.Application ? "Executable" : "DLL");
				OutputString.Append("\t.Linker = '$VCExePath$\\link.exe'\n");
		
				var LinkDefinitions = ActiveProject.ItemDefinitions["Link"];
				string OutputFile = LinkDefinitions.GetMetadataValue("OutputFile").Replace('\\', '/');

				if(HasCompileActions)
				{
					string DependencyOutputPath = LinkDefinitions.GetMetadataValue("ImportLibrary");
					if (Path.IsPathRooted(DependencyOutputPath))
						DependencyOutputPath = DependencyOutputPath.Replace('\\', '/');
					else
						DependencyOutputPath = Path.Combine(ActiveProject.DirectoryPath, DependencyOutputPath).Replace('\\', '/');

					foreach (var deps in CurrentProject.Dependents)
					{
						deps.AdditionalLinkInputs += " \"" + DependencyOutputPath + "\" ";
					}
				}

				ToolTask Task = (ToolTask)Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.Link"));
				string LinkerOptions = GenerateTaskCommandLine(Task, new string[] { "OutputFile", "ProfileGuidedDatabase" }, LinkDefinitions.Metadata);

				if (!string.IsNullOrEmpty(CurrentProject.AdditionalLinkInputs))
				{
					LinkerOptions += CurrentProject.AdditionalLinkInputs;
				}
				OutputString.AppendFormat("\t.LinkerOptions = '\"%1\" /OUT:\"%2\" {0}'\n", LinkerOptions.Replace("'","^'"));
				OutputString.AppendFormat("\t.LinkerOutput = '{0}'\n", OutputFile);

				OutputString.Append("\t.Libraries = { ");
				OutputString.Append(CompileActions);
				OutputString.Append(" }\n");

				OutputString.Append("}\n\n");
			}
			else if(BuildOutput == BuildType.StaticLib)
			{
				OutputString.Append("Library('output')\n{");
				OutputString.Append("\t.Compiler = 'msvc'\n");
				OutputString.Append(string.Format("\t.CompilerOptions = '\"%1\" /Fo\"%2\" /c {0}'\n", CompilerOptions));
				OutputString.Append(string.Format("\t.CompilerOutputPath = \"{0}\"\n", IntDir));
				OutputString.Append("\t.Librarian = '$VCExePath$\\lib.exe'\n");

				var LibDefinitions = ActiveProject.ItemDefinitions["Lib"];
				string OutputFile = LibDefinitions.GetMetadataValue("OutputFile").Replace('\\','/');

				if(HasCompileActions)
				{
					string DependencyOutputPath = "";
					if (Path.IsPathRooted(OutputFile))
						DependencyOutputPath = Path.GetFullPath(OutputFile).Replace('\\', '/');
					else
						DependencyOutputPath = Path.Combine(ActiveProject.DirectoryPath, OutputFile).Replace('\\', '/');

					foreach (var deps in CurrentProject.Dependents)
					{
						deps.AdditionalLinkInputs += " \"" + DependencyOutputPath + "\" ";
					}
				}

				ToolTask task = (ToolTask)Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.LIB"));
				string linkerOptions = GenerateTaskCommandLine(task, new string[] { "OutputFile" }, LibDefinitions.Metadata);
				if(!string.IsNullOrEmpty(CurrentProject.AdditionalLinkInputs))
				{
					linkerOptions += CurrentProject.AdditionalLinkInputs;
				}
				OutputString.AppendFormat("\t.LibrarianOptions = '\"%1\" /OUT:\"%2\" {0}'\n", linkerOptions);
				OutputString.AppendFormat("\t.LibrarianOutput = '{0}'\n", OutputFile);

				OutputString.Append("\t.LibrarianAdditionalInputs = { ");
				OutputString.Append(CompileActions);
				OutputString.Append(" }\n");

				OutputString.Append("}\n\n");
			}

			if (ActiveProject.GetItems("PostBuildEvent").Any())
			{
				ProjectItem BuildEvent = ActiveProject.GetItems("PostBuildEvent").First();
				if (BuildEvent.Metadata.Any())
				{
					ProjectMetadata MetaData = BuildEvent.Metadata.First();
					if(!string.IsNullOrEmpty(MetaData.EvaluatedValue))
					{
						string BatchText = "call \"" + VCBasePath + "Auxiliary\\Build\\vcvarsall.bat\" "
							+ (Platform == "Win32" ? "x86" : "x64") + " " + WindowsSDKTarget + "\n";
						PostBuildBatchFile = Path.Combine(ActiveProject.DirectoryPath, Path.GetFileNameWithoutExtension(ActiveProject.FullPath) + "_postbuild.bat");
						File.WriteAllText(PostBuildBatchFile, BatchText + MetaData.EvaluatedValue);
						OutputString.Append("Exec('postbuild') \n{\n");
						OutputString.AppendFormat("\t.ExecExecutable = '{0}' \n", PostBuildBatchFile);
						OutputString.AppendFormat("\t.ExecInput = '{0}' \n", PostBuildBatchFile);
						OutputString.AppendFormat("\t.ExecOutput = '{0}' \n", PostBuildBatchFile + ".txt");
						OutputString.Append("\t.PreBuildDependencies = 'output' \n");
						OutputString.Append("\t.ExecUseStdOutAsOutput = true \n");
						OutputString.Append("}\n\n");
					}
				}
			}

			OutputString.AppendFormat("Alias ('all')\n{{\n\t.Targets = {{ '{0}' }}\n}}", string.IsNullOrEmpty(PostBuildBatchFile) ? "output" : "postbuild");

			if(FileChanged || CommandLineOptions.AlwaysRegenerate)
			{
				File.WriteAllText(BFFOutputFilePath, OutputString.ToString());
			}		   
		}

		public static string GenerateTaskCommandLine(
			ToolTask Task,
			string[] PropertiesToSkip,
			IEnumerable<ProjectMetadata> MetaDataList)
		{
			foreach (ProjectMetadata MetaData in MetaDataList)
			{
				if (PropertiesToSkip.Contains(MetaData.Name))
					continue;

				var MatchingProps = Task.GetType().GetProperties().Where(prop => prop.Name == MetaData.Name);
				if (MatchingProps.Any() && !string.IsNullOrEmpty(MetaData.EvaluatedValue))
				{
					string EvaluatedValue = MetaData.EvaluatedValue.Trim();
					if(MetaData.Name == "AdditionalIncludeDirectories")
					{
						EvaluatedValue = EvaluatedValue.Replace("\\\\", "\\");
						EvaluatedValue = EvaluatedValue.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
					}

					PropertyInfo propInfo = MatchingProps.First(); //Dubious
					if (propInfo.PropertyType.IsArray && propInfo.PropertyType.GetElementType() == typeof(string))
					{
						propInfo.SetValue(Task, Convert.ChangeType(EvaluatedValue.Split(';'), propInfo.PropertyType));
					}
					else
					{
						propInfo.SetValue(Task, Convert.ChangeType(EvaluatedValue, propInfo.PropertyType));
					}
				}
			}

			var GenCmdLineMethod = Task.GetType().GetRuntimeMethods().Where(meth => meth.Name == "GenerateCommandLine").First(); //Dubious
			return GenCmdLineMethod.Invoke(Task, new object[] { Type.Missing, Type.Missing }) as string;
		}
	}

}
