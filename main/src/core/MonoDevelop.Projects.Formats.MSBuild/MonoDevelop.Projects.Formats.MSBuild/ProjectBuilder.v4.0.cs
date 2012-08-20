// 
// ProjectBuilder.cs
//  
// Author:
//       Lluis Sanchez Gual <lluis@novell.com>
// 
// Copyright (c) 2009 Novell, Inc (http://www.novell.com)
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Threading;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using System.Runtime.Remoting;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using Microsoft.Build.Logging;
using Microsoft.Build.Execution;

namespace MonoDevelop.Projects.Formats.MSBuild
{
	public class ProjectBuilder: MarshalByRefObject, IProjectBuilder
	{
		Project project;
		ProjectCollection engine;
		string file;
		ILogWriter currentLogWriter;
		ConsoleLogger consoleLogger;
		string currentConfiguration, currentPlatform;

		AutoResetEvent wordDoneEvent = new AutoResetEvent (false);
		ThreadStart workDelegate;
		object workLock = new object ();
		Thread workThread;
		Exception workError;

		public ProjectBuilder (string file, string binDir)
		{
			this.file = file;
			RunSTA (delegate
			{
				engine = new ProjectCollection ();
				engine.SetGlobalProperty ("BuildingInsideVisualStudio", "true");
				
				//we don't have host compilers in MD, and this is set to true by some of the MS targets
				//which causes it to always run the CoreCompile task if BuildingInsideVisualStudio is also
				//true, because the VS in-process compiler would take care of the deps tracking
				engine.SetGlobalProperty ("UseHostCompilerIfAvailable", "false");

				consoleLogger = new ConsoleLogger (LoggerVerbosity.Normal, LogWriteLine, null, null);
				engine.RegisterLogger (consoleLogger);
			});
			
			Refresh ();
		}
		
		public void Refresh ()
		{
			RunSTA (delegate
			{
				//just unload the project. it will be reloaded when we next build
				project = null;
				var loadedProj = engine.GetLoadedProjects (file).FirstOrDefault ();
				if (loadedProj != null) {
					engine.UnloadProject (loadedProj);
					engine.UnloadProject (loadedProj.Xml);
				}
			});
		}
		
		void LogWriteLine (string txt)
		{
			if (currentLogWriter != null)
				currentLogWriter.WriteLine (txt);
		}
		
		public MSBuildResult[] RunTarget (string target, string configuration, string platform, ILogWriter logWriter,
			MSBuildVerbosity verbosity)
		{
			MSBuildResult[] result = null;
			RunSTA (delegate
			{
				try {
					SetupProject (configuration, platform);
					currentLogWriter = logWriter;

					LocalLogger logger = new LocalLogger (Path.GetDirectoryName (file));
					engine.RegisterLogger (logger);

					consoleLogger.Verbosity = GetVerbosity (verbosity);
					
					project.Build (target);
					
					result = logger.BuildResult.ToArray ();
		//		} catch (InvalidProjectFileException ex) {
		//			result = new MSBuildResult[] { new MSBuildResult (false, ex.ProjectFile ?? file, ex.LineNumber, ex.ColumnNumber, ex.ErrorCode, ex.Message) };
				} finally {
					currentLogWriter = null;
				}
			});
			return result;
		}
		
		LoggerVerbosity GetVerbosity (MSBuildVerbosity verbosity)
		{
			switch (verbosity) {
			case MSBuildVerbosity.Quiet:
				return LoggerVerbosity.Quiet;
			case MSBuildVerbosity.Minimal:
				return LoggerVerbosity.Minimal;
			case MSBuildVerbosity.Normal:
			default:
				return LoggerVerbosity.Normal;
			case MSBuildVerbosity.Detailed:
				return LoggerVerbosity.Detailed;
			case MSBuildVerbosity.Diagnostic:
				return LoggerVerbosity.Diagnostic;
			}
		}

		public string[] GetAssemblyReferences (string configuration, string platform)
		{
			string[] refsArray = null;

			RunSTA (delegate
			{
				SetupProject (configuration, platform);
				
				// We are using this BuildProject overload and the BuildSettings.None argument as a workaround to
				// an xbuild bug which causes references to not be resolved after the project has been built once.
				var pi = project.CreateProjectInstance ();
				pi.Build ("ResolveAssemblyReferences", null);
				List<string> refs = new List<string> ();
				foreach (ProjectItemInstance item in pi.GetItems ("ReferencePath"))
					refs.Add (UnescapeString (item.EvaluatedInclude));
				refsArray = refs.ToArray ();
			});
			return refsArray;
		}
		
		void SetupProject (string configuration, string platform)
		{
			if (project != null && configuration == currentConfiguration && platform == currentPlatform)
				return;
			currentConfiguration = configuration;
			currentPlatform = platform;
			
			Environment.CurrentDirectory = Path.GetDirectoryName (file);
			engine.SetGlobalProperty ("Configuration", configuration);
			if (!string.IsNullOrEmpty (platform))
				engine.SetGlobalProperty ("Platform", platform);
			else
				engine.RemoveGlobalProperty ("Platform");

			project = engine.LoadProject (file);
		}
		
		public override object InitializeLifetimeService ()
		{
			return null;
		}

		void RunSTA (ThreadStart ts)
		{
			lock (workLock) {
				lock (threadLock) {
					workDelegate = ts;
					workError = null;
					if (workThread == null) {
						workThread = new Thread (STARunner);
						workThread.SetApartmentState (ApartmentState.STA);
						workThread.IsBackground = true;
						workThread.Start ();
					}
					else
						// Awaken the existing thread
						Monitor.Pulse (threadLock);
				}
				wordDoneEvent.WaitOne ();
			}
			if (workError != null)
				throw new Exception ("MSBuild operation failed", workError);
		}

		object threadLock = new object ();

		void STARunner ()
		{
			lock (threadLock) {
				do {
					try {
						workDelegate ();
					}
					catch (Exception ex) {
						workError = ex;
					}
					wordDoneEvent.Set ();
				}
				while (Monitor.Wait (threadLock, 60000));

				workThread = null;
			}
		}
		
		//from MSBuildProjectService
		static string UnescapeString (string str)
		{
			int i = str.IndexOf ('%');
			while (i != -1 && i < str.Length - 2) {
				int c;
				if (int.TryParse (str.Substring (i+1, 2), System.Globalization.NumberStyles.HexNumber, null, out c))
					str = str.Substring (0, i) + (char) c + str.Substring (i + 3);
				i = str.IndexOf ('%', i + 1);
			}
			return str;
		}
	}
}
