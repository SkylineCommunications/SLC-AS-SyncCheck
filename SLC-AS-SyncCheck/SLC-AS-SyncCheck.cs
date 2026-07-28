/*
***********************************************
*  Copyright (c), Skyline Communications NV.  *
***********************************************

Revision History:

DATE		VERSION		AUTHOR			COMMENTS

17-10-2022	1.0.0		Skyline			Initial version
28/07/2026	1.0.1		LFR, Skyline	Parse DOMAIN\username from the Username parameter so AD users authenticate without a separate Domain param (task 303353).
										Fixed Backup domain resolution: was using Main DMA IP/ComputerName instead of Backup IP/name (task 303353).
										Renamed output to SyncCheckResult_YYYYMMDD_HHMM.txt under C:\Skyline_Data\SyncCheckResults\; timestamp uses server-local time.
										Added DEBUG line with resolved connection identity (DOMAIN\username + IP) for Main and Backup DMAs.
										Normalized parser error logging to one compact line per failure: '<msg> -> <ExceptionType>: <Message>'.
										Grouped ERRORS section by source DMA (MAIN/BACKUP -> IP ComputerName) so operators can attribute each error without inspecting the UNC path.
										Enriched ELEMENT/Service/RemoteService folder-info sections with the DMA ComputerName; unified format as "'<ComputerName>' => <IP>".
										INFO sections rewritten: section header shows FO pair "(FO Pair: 'A' <-> 'B')"; per-block separators name verb + agent + role.
										Added ComputerName to remaining section headers (Elements INFO, Service INFO, Sync Info Folder Check for Main/Backup DMA).
										GetServicesBackup duplicate-entry error mislabeled as "Main Service folder"; corrected to "Backup Service folder".
										Fixed Main/Backup ComputerName swap when SLNet responded via the Backup agent; roles now normalized using FailoverIsBackup.
										Standardized offline-partner terminology on "Backup" in operator-facing output; "Failover" now refers only to the pair/feature.
										SyncInfoDetails.GetDetailsFromFile guard now checks change.Attribute("type") (lowercase) to match the read and actual SyncInfo.xml casing.
										Added Script.ScriptVersion const and injected it into the DEBUG header so each output identifies which script version produced it.
****************************************************************************
*/

using System;
using System.Globalization;
using Skyline.Automation.Testing;
using Skyline.DataMiner.Automation;

public class Script
{
	public const string ScriptVersion = "1.0.1";

	public void Run(Engine engine)
	{
		engine.Timeout = new TimeSpan(0, 45, 0);
		ScriptData scriptdata = new ScriptData(engine);
		try
		{
			string rawUser = engine.GetScriptParam("Username").Value;
			string pass = engine.GetScriptParam("Password").Value;

			// Parse supported Windows credential formats:
			//   "alice"                    -> local (domain = "")
			//   ".\alice"                  -> local (".\" stripped)
			//   "MYDOMAIN\alice"           -> NETBIOS down-level (domain = "MYDOMAIN")
			//   "alice@corp.example.com"   -> UPN, pass through as user, empty domain (SSPI resolves)
			//   malformed ("\x", "x\", "") -> fall through as-is; auth will surface the error
			string domain = String.Empty;
			string trimmed = (rawUser ?? String.Empty).Trim();
			string user = trimmed;
			int sep = trimmed.IndexOf('\\');
			if (sep > 0 && sep < trimmed.Length - 1)
			{
				string prefix = trimmed.Substring(0, sep);
				user = trimmed.Substring(sep + 1);
				// "." is the Windows alias for the local machine — treat as local (no domain).
				if (!prefix.Equals(".", StringComparison.Ordinal))
				{
					domain = prefix;
				}
			}
			// UPN (user@domain) needs no split: WNetUseConnection/SSPI accepts the full UPN as the username with an empty domain.

			DMSHelper2 DMS = new DMSHelper2(scriptdata, domain, user, pass);

			DMS.CheckFolders(scriptdata);
		}
		catch (Exception e)
		{
			engine.GenerateInformation("Exception: " + e.ToString());
		}
		finally
		{
			string destFolder = @"C:\Skyline_Data\SyncCheckResults\";
			Skyline.DataMiner.Net.Tools.EnsureDirectoryExists(destFolder);
			string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture);
			scriptdata.Log(destFolder + "SyncCheckResult_" + timestamp + ".txt");
		}
	}
}

namespace Skyline.Automation.Testing
{
	using System;
	using System.Collections.Generic;
	using System.ComponentModel;
	using System.Globalization;
	using System.IO;
	using System.Linq;
	using System.Net;
	using System.Runtime.InteropServices;
	using System.Text;
	using System.Xml;
	using System.Xml.Linq;
	using Skyline.DataMiner.Automation;
	using Skyline.DataMiner.Net.Messages;

	public enum LogLevel
	{
		All = 0,
		Debug1 = 1
	}

	public class ScriptData
	{
		public Engine engine { get; set; }
		public List<string> lErrors { get; set; }
		public List<string> lDebug { get; set; }
		public List<string> lDebugCompleetParts { get; set; }
		public List<DMAIdentity> dmaIdentities { get; set; }

		public ScriptData(Engine engine)
		{
			lErrors = new List<string>();
			lDebug = new List<string>();
			lDebugCompleetParts = new List<string>();
			dmaIdentities = new List<DMAIdentity>();
			this.engine = engine;
		}

		public void AddError(string sError)
		{
			lErrors.Add(sError);
		}
		public void AddDebug(string sDebug)
		{
			lDebug.Add(sDebug);
		}
		public void AddDebugCompleetPart(string sDebugCompleetPart)
		{
			lDebugCompleetParts.Add(sDebugCompleetPart);
		}
		public class DMAIdentity
		{
			public string Role { get; set; }
			public string IP { get; set; }
			public string ComputerName { get; set; }
			public string Header
			{
				get
				{
					string ipDisp = IP ?? "LOCAL";
					string cn = string.IsNullOrEmpty(ComputerName) ? "?" : "'" + ComputerName + "'";
					return "--- " + Role + ": " + cn + " => " + ipDisp + " ---";
				}
			}
		}

		public void RegisterDMA(string role, string ip, string computerName)
		{
			dmaIdentities.Add(new DMAIdentity { Role = role, IP = ip, ComputerName = computerName });
		}

		public string GetErrorText()
		{
			if (dmaIdentities == null || dmaIdentities.Count == 0 || lErrors.Count == 0)
			{
				return GetLogging("ERRORS", lErrors.ToArray());
			}

			StringBuilder sb = new StringBuilder();
			sb.Append(GetHeader("ERRORS"));

			List<string> remaining = new List<string>(lErrors);
			bool anyPrinted = false;
			foreach (DMAIdentity id in dmaIdentities)
			{
				List<string> matched = new List<string>();
				if (!string.IsNullOrEmpty(id.IP))
				{
					string needle = @"\\" + id.IP + @"\";
					for (int i = remaining.Count - 1; i >= 0; i--)
					{
						if (remaining[i].IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
						{
							matched.Insert(0, remaining[i]);
							remaining.RemoveAt(i);
						}
					}
				}
				if (matched.Count > 0)
				{
					sb.Append(id.Header).Append(Environment.NewLine);
					sb.Append(string.Join(Environment.NewLine, matched.ToArray())).Append(Environment.NewLine);
					anyPrinted = true;
				}
			}

			if (remaining.Count > 0)
			{
				sb.Append("--- OTHER ---").Append(Environment.NewLine);
				sb.Append(string.Join(Environment.NewLine, remaining.ToArray())).Append(Environment.NewLine);
				anyPrinted = true;
			}

			if (!anyPrinted)
			{
				sb.Append("No ERRORS").Append(Environment.NewLine);
			}
			sb.Append(GetTrailer());
			return sb.ToString();
		}
		public string GetDebugText()
		{
			return GetLogging("DEBUG LOGGING", lDebug.ToArray());
		}

		public void Log(string sPath)
		{
			System.IO.File.WriteAllText(sPath, GetErrorText() + GetDebugText() + string.Join(Environment.NewLine, lDebugCompleetParts));
		}

		public static string GetHeader(string Title)
		{
			return string.Format("{0}{1}{2}{3}{1}{0}{1}", "#################################", Environment.NewLine, "####", Title);
		}
		public static string GetTrailer()
		{
			return "---------------------------------" + Environment.NewLine;
		}
		public static string GetLogging(string Title, string[] logParts)
		{
			StringBuilder sb = new StringBuilder();
			sb.Append(GetHeader(Title));
			if (logParts.Length > 0)
			{
				sb.Append(string.Join(Environment.NewLine, logParts));
			}
			else
			{
				sb.Append("No " + Title);
			}
			sb.Append(Environment.NewLine).Append(GetTrailer());
			return sb.ToString();
		}
	}

	internal class AlarmHelper
	{
		public class Alarm : IEquatable<Alarm>
		{
			public int iDataMinerID { get; set; }
			public int iElementID { get; set; }
			public string sAlarmID { get; set; }
			public string sRootAlarmID { get; set; }

			public Alarm(int iDmaId, int iEId, string sAId, string sRAId)
			{
				iDataMinerID = iDmaId;
				iElementID = iEId;
				sAlarmID = Convert.ToString(iDmaId) + "_" + sAId;
				sRootAlarmID = Convert.ToString(iDmaId) + "_" + sRAId;
			}

			public Alarm(string sDmaId, string sEId, string sAId, string sRAId)
			{
				iDataMinerID = Convert.ToInt32(sDmaId);
				iElementID = Convert.ToInt32(sEId);
				sAlarmID = sAId;
				sRootAlarmID = sRAId;
			}

			public bool Equals(Alarm compare)
			{
				if (compare == null)
					return false;

				return
					(
					object.ReferenceEquals(this.iDataMinerID, compare.iDataMinerID) ||
					this.iDataMinerID.Equals(compare.iDataMinerID)
					)
					&&
					(
					object.ReferenceEquals(this.iElementID, compare.iElementID) ||
					this.iElementID.Equals(compare.iElementID)
					)
					&&
					(
					object.ReferenceEquals(this.sAlarmID, compare.sAlarmID) ||
					this.sAlarmID != null &&
					this.sAlarmID.Equals(compare.sAlarmID)
					)
					&&
					(
					object.ReferenceEquals(this.sRootAlarmID, compare.sRootAlarmID) ||
					this.sRootAlarmID != null &&
					this.sRootAlarmID.Equals(compare.sRootAlarmID)
					);
			}

			public string GetInfo()
			{
				return string.Format("{0}/{1} with root '{2}' and id '{3}'", iDataMinerID, iElementID, sRootAlarmID, sAlarmID);
			}
		}

		public class AlarmTree
		{
			public const string NullValue = "<NULL>";
			public string sRootAlarm { get; set; }
			public int DMAID { get; set; }
			public int ELID { get; set; }
			public Alarm RootAlarm { get; set; }
			public List<Alarm> AlarmHistory { get; set; }

			public AlarmTree(string sRootAlarm, int DMAID, int ELID)
			{
				RootAlarm = null;
				AlarmHistory = new List<Alarm>();
				this.sRootAlarm = sRootAlarm;
				this.DMAID = DMAID;
				this.ELID = ELID;
			}

			public bool addAlarm(Alarm alarm)
			{
				if (alarm.iDataMinerID != DMAID || alarm.iElementID != ELID)
				{
					return false;
				}
				if (RootAlarm == null && alarm.sAlarmID.Equals(sRootAlarm))
				{
					RootAlarm = alarm;
					return true;
				}
				else if (!AlarmHistory.Contains(alarm) && sRootAlarm.Equals(alarm.sRootAlarmID))
				{
					AlarmHistory.Add(alarm);
					return true;
				}
				return false;
			}

			public string GetInfo(LogLevel Level)
			{
				if (Level == LogLevel.All)
				{
					return string.Format("Root: {0} | with {3} childalarms{1}{2}", RootAlarm == null ? NullValue : RootAlarm.GetInfo(), Environment.NewLine + "   ", string.Join(Environment.NewLine + "   ", AlarmHistory.Select(s => s.GetInfo())), AlarmHistory.Count());
				}
				else
				{
					if (RootAlarm == null)
					{
						return string.Format("Root: <NULL> sRoot: {0} with {1} childalarms", sRootAlarm, AlarmHistory.Count());
					}
					else
					{
						return string.Format("Root: {0} | with {1} childalarms", RootAlarm == null ? NullValue : RootAlarm.GetInfo(), AlarmHistory.Count());
					}
				}
			}
			public int GetChildCount() { return AlarmHistory.Count(); }
		}

		public class AlarmContainer
		{
			public Dictionary<string, AlarmTree> dictAlarmTrees { get; set; }
			int iAlarmProccesed = 0;
			public AlarmContainer() { dictAlarmTrees = new Dictionary<string, AlarmTree>(); }

			public bool ProccesAlarm(Alarm alarm)
			{
				if (!dictAlarmTrees.ContainsKey(alarm.sRootAlarmID))
				{
					dictAlarmTrees[alarm.sRootAlarmID] = new AlarmTree(alarm.sRootAlarmID, alarm.iDataMinerID, alarm.iElementID);
				}
				iAlarmProccesed++;
				return dictAlarmTrees[alarm.sRootAlarmID].addAlarm(alarm);
			}

			public void AddTree(AlarmTree alarmTree)
			{
				dictAlarmTrees[alarmTree.sRootAlarm] = alarmTree;
			}
			public void AddInfo(ScriptData scriptdata, string sTitle)
			{
				scriptdata.AddDebugCompleetPart(ScriptData.GetLogging(sTitle, dictAlarmTrees.Values.Select(s => s.GetInfo(LogLevel.Debug1)).ToArray()));
			}
			public void AddStats(ScriptData scriptdata, string sTitle)
			{

				int ChildAlarmCount = 0;
				int MaxChilds = 0;
				int RootAlarmCount = 0;
				int CountWithoutRoot = 0;
				if (dictAlarmTrees == null)
				{
					scriptdata.AddError("dictAlarmTrees is <NULL>");
					return;
				}
				int TreeAmount = dictAlarmTrees.Count();

				foreach (var tree in dictAlarmTrees.Values)
				{
					int iTreeChilds = tree.GetChildCount();
					if (iTreeChilds > MaxChilds)
					{
						MaxChilds = iTreeChilds;
					}
					ChildAlarmCount = ChildAlarmCount + iTreeChilds;
					if (tree.RootAlarm != null)
					{
						RootAlarmCount++;
					}
					else
					{
						CountWithoutRoot++;
					}
				}

				StringBuilder sb = new StringBuilder();
				sb.Append(ScriptData.GetHeader(sTitle));
				sb.Append("Total amount of processed alarms: ").Append(iAlarmProccesed).Append(Environment.NewLine);
				sb.Append("Total amount of alarms: ").Append(RootAlarmCount + ChildAlarmCount).Append(Environment.NewLine);
				sb.Append("Total amount of Trees: ").Append(TreeAmount).Append(Environment.NewLine);
				sb.Append("Total amount of Roots: ").Append(RootAlarmCount).Append(Environment.NewLine);
				sb.Append("Total amount of Trees without root: ").Append(CountWithoutRoot).Append(Environment.NewLine);
				sb.Append("Total amount Childs: ").Append(ChildAlarmCount).Append(Environment.NewLine);
				sb.Append("Max History: ").Append(MaxChilds).Append(Environment.NewLine);
				sb.Append(ScriptData.GetTrailer());

				scriptdata.AddDebugCompleetPart(sb.ToString());
			}
		}
	}

	/// <summary>
	/// This Class is used to retrieve element info from SLNet.
	/// </summary>
	internal class ElementHelper
	{
		public class ScriptElement : IEquatable<ScriptElement>
		{
			public int iDataMinerID { get; set; }
			public int iHostedDMAID { get; set; }
			public int iElementID { get; set; }
			public string ProtocolName { get; set; }
			public string Version { get; set; }
			public string Name { get; set; }
			public bool DVE { get; set; }
			public bool Service { get; set; }

			public ScriptElement(LiteServiceElementInfoEvent elService)
			{
				iDataMinerID = elService.DataMinerID;
				iHostedDMAID = elService.HostingAgentID;
				iElementID = elService.ElementID;
				ProtocolName = elService.Protocol;
				Version = elService.ProtocolVersion;
				Name = elService.Name;
				Service = true;
				if (elService.DveParentElementId == 0 && elService.DveParentDmaId == 0)
				{ DVE = false; }
				else
				{ DVE = true; }
			}
			public ScriptElement(LiteElementInfoEvent elElement)
			{
				iDataMinerID = elElement.DataMinerID;
				iHostedDMAID = elElement.HostingAgentID;
				iElementID = elElement.ElementID;
				ProtocolName = elElement.Protocol;
				Version = elElement.ProtocolVersion;
				Name = elElement.Name;
				Service = false;
				if (elElement.DveParentElementId == 0 && elElement.DveParentDmaId == 0)
				{ DVE = false; }
				else
				{ DVE = true; }
			}
			public bool Equals(ScriptElement compare)
			{
				if (compare == null)
				{
					return false;
				}
				if (iDataMinerID == compare.iDataMinerID && iElementID == compare.iElementID && ProtocolName.Equals(compare.ProtocolName) && Version.Equals(compare.Version) && Name.Equals(compare.Name))
				{
					return true;
				}
				return false;
			}
		}

		public static DMSMessage[] GetElements(bool IncludeHidden, bool IncludePaused, bool IncludeServiceElements, bool IncludeStopped, bool includeAggregator, int DMAID = -1)
		{
			var Msg = new GetLiteElementInfo { IncludeAggregatorElements = includeAggregator, IncludeHidden = IncludeHidden, IncludePaused = IncludePaused, IncludeServiceElements = IncludeServiceElements, IncludeStopped = IncludeStopped };
			if (DMAID != -1)
			{
				Msg.DataMinerID = DMAID;
			}

			var Response = Engine.SLNet.SendMessage(Msg);
			return Response;
		}

		public class ElementContainer
		{
			public Dictionary<string, ScriptElement> dictElements { get; set; }
			public ElementContainer()
			{
				dictElements = new Dictionary<string, ScriptElement>();
			}

			public void PopulateFromSLNet(ScriptData scriptdata, int DMAID = -1)
			{
				dictElements = new Dictionary<string, ScriptElement>();
				var Response = GetElements(true, true, true, true, true, DMAID);
				foreach (var msg in Response)
				{
					if (msg is LiteServiceElementInfoEvent)
					{
						var elService = msg as LiteServiceElementInfoEvent;
						dictElements[elService.DataMinerID + "/" + elService.ElementID] = new ScriptElement(elService);

					}
					else if (msg is LiteElementInfoEvent)
					{
						var elElement = msg as LiteElementInfoEvent;
						dictElements[elElement.DataMinerID + "/" + elElement.ElementID] = new ScriptElement(elElement);
					}
					else
					{
						scriptdata.AddError("PopulateFromSLNet|Could not convert DMSMessage: " + msg.GetType());
					}
				}
			}
		}
	}

	/// <summary>
	/// This Class is used to retrieve service info from SLNet.
	/// </summary>
	internal class ServiceHelper
	{
		public class ScriptService
		{
			public int iDataMinerID { get; set; }
			public int iHostedDMAID { get; set; }
			public int iServiceID { get; set; }
			public string Name { get; set; }
			public bool Enhanced { get; set; }
			public int iEnhencedDMAID { get; set; }
			public int iEnhencedELID { get; set; }
			public ScriptService(LiteServiceInfoEvent liteService)
			{
				iDataMinerID = liteService.DataMinerID;
				iHostedDMAID = liteService.HostingAgentID;
				iServiceID = liteService.ID;
				Name = liteService.Name;
				if (liteService.ServiceElement != null)
				{
					Enhanced = true;
					iEnhencedDMAID = liteService.ServiceElement.DataMinerID;
					iEnhencedELID = liteService.ServiceElement.ElementID;
				}
				else
				{
					Enhanced = false;
					iEnhencedDMAID = -1;
					iEnhencedELID = -1;
				}
			}
		}
		public static DMSMessage[] GetServices(int DMAID)
		{
			var Msg = new GetLiteServiceInfo { DataMinerID = DMAID };
			var Response = Engine.SLNet.SendMessage(Msg);
			return Response;
		}
		public static DMSMessage[] GetServices()
		{
			var Msg = new GetLiteServiceInfo();
			var response = Engine.SLNet.SendMessage(Msg);
			return response;
		}
		public class ServiceContainer
		{
			public Dictionary<string, ScriptService> dictServices { get; set; }
			public ServiceContainer()
			{
				dictServices = new Dictionary<string, ScriptService>();
			}

			public void PopulateFromSLNet(ScriptData scriptdata, int DMAID)
			{
				dictServices = new Dictionary<string, ScriptService>();
				var Response = GetServices(DMAID);
				foreach (var msg in Response)
				{
					if (msg is LiteServiceInfoEvent)
					{
						var elService = msg as LiteServiceInfoEvent;
						dictServices[elService.DataMinerID + "/" + elService.ElementID] = new ScriptService(elService);
					}
					else
					{
						scriptdata.AddError("PopulateFromSLNet|Could not convert DMSMessage: " + msg.GetType());
					}
				}
			}

			public void PopulateFromSLNet(ScriptData scriptdata)
			{
				dictServices = new Dictionary<string, ScriptService>();
				var Response = GetServices();
				foreach (var msg in Response)
				{
					if (msg is LiteServiceInfoEvent)
					{
						var elService = msg as LiteServiceInfoEvent;
						dictServices[elService.DataMinerID + "/" + elService.ElementID] = new ScriptService(elService);
					}
					else
					{
						scriptdata.AddError("PopulateFromSLNet|Could not convert DMSMessage: " + msg.GetType());
					}
				}
			}
		}
	}

	public class DMSHelper2
	{
		private Dictionary<int, DMAFolderHelper> DMAs { get; set; }
		private ServiceHelper.ServiceContainer allSLNetServices { get; set; }
		private ElementHelper.ElementContainer allSLNetElements { get; set; }

		public DMSHelper2(ScriptData scriptdata, string domain, string username, string password, bool bSyncInfo = false)
		{
			scriptdata.AddDebug("Start Retrieving Cluster DMA's (Sync Check v" + Script.ScriptVersion + ") " + DateTime.Now);
			DMAs = new Dictionary<int, DMAFolderHelper>();
			var Msg = new GetInfoMessage(InfoType.DataMinerInfo);
			var Response = Engine.SLNet.SendMessage(Msg);
			for (int i = 0; i < Response.Length; i++)
			{
				var InfoMsg = Response[i] as GetDataMinerInfoResponseMessage;
				if (InfoMsg.FailoverRawIPs == null || InfoMsg.FailoverRawIPs.Length == 0)
				{
					scriptdata.AddDebug(string.Format("Found DMA without Failover {0}: '{1}' => {2}", InfoMsg.ID, InfoMsg.ComputerName, InfoMsg.PrimaryIP));
					DMAs[InfoMsg.ID] = new DMAFolderHelper(InfoMsg.ID, InfoMsg.PrimaryIP, null, InfoMsg.ComputerName, null, domain, username, password, bSyncInfo);
				}
				else // Failover DMA active
				{
					var IPs = InfoMsg.FailoverRawIPs;
					if (IPs.Length == 2)
					{
						string mainName = InfoMsg.FailoverIsBackup ? InfoMsg.FailoverPartnerName : InfoMsg.ComputerName;
						string backupName = InfoMsg.FailoverIsBackup ? InfoMsg.ComputerName : InfoMsg.FailoverPartnerName;
						scriptdata.AddDebug(String.Format("Found Failover Pair {0}: Main '{1}' => {2}, Backup '{3}' => {4}", InfoMsg.ID, mainName, IPs[0], backupName, IPs[1]));
						DMAs[InfoMsg.ID] = new DMAFolderHelper(InfoMsg.ID, IPs[0], IPs[1], mainName, backupName, domain, username, password, bSyncInfo);
					}
					else
					{
						scriptdata.AddError("DMSHelper|Failed to parse failoverIPs (" + IPs.Length + ").");
					}
				}
			}

			allSLNetServices = new ServiceHelper.ServiceContainer();
			allSLNetServices.PopulateFromSLNet(scriptdata);

			allSLNetElements = new ElementHelper.ElementContainer();
			allSLNetElements.PopulateFromSLNet(scriptdata);
		}

		public void CheckFolders(ScriptData scriptdata)
		{
			// Retrieve and compare elements/services in folders (Main/Backup) and in SLNet.
			foreach (var DMA in DMAs.Values)
			{
				DMA.RetrieveInfoFromFolders(scriptdata, allSLNetElements.dictElements, allSLNetServices.dictServices);
			}

			// Compare services from DMA's with the services in the remote service folder now all the info is retrieved from all the DMA's
			Dictionary<int, Dictionary<string, DMAFolderHelper.ServiceDetails>> dictFoundServices = new Dictionary<int, Dictionary<string, DMAFolderHelper.ServiceDetails>>();
			Dictionary<int, Dictionary<string, DMAFolderHelper.ElementDetails>> dictFoundElements = new Dictionary<int, Dictionary<string, DMAFolderHelper.ElementDetails>>();

			foreach (var DMA in DMAs.Values)
			{
				dictFoundServices[DMA.DMAID] = DMA.ServicesMain;
				dictFoundElements[DMA.DMAID] = DMA.ElementsMain;
			}

			foreach (var DMA in DMAs.Values)
			{
				DMA.CompareRemServicesWithDMAs(scriptdata, dictFoundServices);
				if (DMA.SyncInfo)
				{
					DMA.CompareSyncFileMain(scriptdata, dictFoundServices, dictFoundElements);
					DMA.CompareSyncFileBackup(scriptdata, dictFoundServices, dictFoundElements);
				}
			}


		}

		internal class DMAFolderHelper
		{
			#region Fields and Properties
			private bool ElementSync = false;
			private bool ServiceSync = false;
			public bool SyncInfo { get; set; }
			public string IP { get; set; }
			public string FailoverIP { get; set; }
			public string ComputerName { get; set; }
			public string FailoverName { get; set; }
			public int DMAID { get; set; }
			public string domain { get; set; }
			public string username { get; set; }
			public string password { get; set; }
			public string basePathMain { get; set; }
			public string basePathBackup { get; set; }
			public Dictionary<string, ElementDetails> ElementsMain { get; set; }
			public Dictionary<string, ElementDetails> ElementsBackup { get; set; }
			public Dictionary<string, ServiceDetails> ServicesMain { get; set; }
			public Dictionary<string, ServiceDetails> ServicesBackup { get; set; }
			public Dictionary<int, Dictionary<string, ServiceDetails>> dictRemoteServicesMain { get; set; }
			public Dictionary<int, Dictionary<string, ServiceDetails>> dictRemoteServicesBackup { get; set; }
			public Dictionary<string, SyncInfoDetails> dictSyncEntriesMain { get; set; }
			public Dictionary<string, SyncInfoDetails> dictSyncEntriesBackup { get; set; }
			#endregion

			public DMAFolderHelper(int iDMAID, string sIP, string sFailoverIP, string sComputerName, string sFailoverName, string domain, string username, string password, bool bSyncInfo = false)
			{
				SyncInfo = bSyncInfo;
				IP = sIP;
				FailoverIP = sFailoverIP;
				DMAID = iDMAID;
				this.domain = domain;
				this.username = username;
				this.password = password;
				this.ComputerName = sComputerName;
				this.FailoverName = sFailoverName;
			}

			#region GetInfoFromFolders
			private void GetElementsMain(ScriptData scriptdata)
			{
				ElementSync = false;
				string ElementFolderPath = basePathMain + @"\Elements";
				string[] ElementPathsMain = Directory.GetDirectories(ElementFolderPath);
				ElementsMain = new Dictionary<string, ElementDetails>();
				Dictionary<string, string> dictIDs = new Dictionary<string, string>();
				Dictionary<string, string[]> dictDuplIDs = new Dictionary<string, string[]>();
				foreach (var path in ElementPathsMain)
				{
					string FolderName = path.Replace(ElementFolderPath + @"\", string.Empty);
					var element = ElementDetails.GetElementDetails(scriptdata, path + @"\Element.xml");
					if (element != null)
					{
						string sID = element.DMAID + "/" + element.ELID;
						if (!dictIDs.ContainsKey(sID))
						{
							dictIDs[sID] = FolderName;
						}
						else
						{
							if (dictDuplIDs.ContainsKey(sID))
							{
								string[] asDupFolders = new string[dictDuplIDs[sID].Length + 1];
								for (int i = 0; i < asDupFolders.Length - 1; i++)
								{
									asDupFolders[i] = dictDuplIDs[sID][i];
								}
								asDupFolders[asDupFolders.Length - 1] = FolderName;
								dictDuplIDs[sID] = asDupFolders;
							}
							else
							{
								dictDuplIDs[sID] = new string[] { FolderName, dictIDs[sID] };
							}
						}
					}

					if (element != null && FolderName != element.Name)
					{
						if (element.Name.ToLower() != FolderName.ToLower())
						{
							scriptdata.AddError("Inconsistent FolderName (" + element.Name + "): " + path);
						}
						else
						{
							scriptdata.AddError("Capitalization FolderName (" + element.Name + "): " + path);
						}
					}
					ElementsMain[FolderName] = element;
				}

				foreach (var dupl in dictDuplIDs)
				{
					scriptdata.AddError(string.Format("Duplicate Entries in Main Elements folder for ID {0}:{1}", dupl.Key, string.Join(", ", dupl.Value)));
				}
			}

			private void GetElementsBackup(ScriptData scriptdata)
			{
				ElementSync = false;
				string ElementFolderPath = basePathBackup + @"\Elements";
				string[] ElementPathsBackup = Directory.GetDirectories(ElementFolderPath);
				ElementsBackup = new Dictionary<string, ElementDetails>();
				Dictionary<string, string> dictIDs = new Dictionary<string, string>();
				Dictionary<string, string[]> dictDuplIDs = new Dictionary<string, string[]>();
				foreach (var path in ElementPathsBackup)
				{
					string FolderName = path.Replace(ElementFolderPath + @"\", string.Empty);
					var element = ElementDetails.GetElementDetails(scriptdata, path + @"\Element.xml");
					if (element != null)
					{
						string sID = element.DMAID + "/" + element.ELID;
						if (!dictIDs.ContainsKey(sID))
						{
							dictIDs[sID] = FolderName;
						}
						else
						{
							if (dictDuplIDs.ContainsKey(sID))
							{
								string[] asDupFolders = new string[dictDuplIDs[sID].Length + 1];
								for (int i = 0; i < asDupFolders.Length - 1; i++)
								{
									asDupFolders[i] = dictDuplIDs[sID][i];
								}
								asDupFolders[asDupFolders.Length - 1] = FolderName;
								dictDuplIDs[sID] = asDupFolders;
							}
							else
							{
								dictDuplIDs[sID] = new string[] { FolderName, dictIDs[sID] };
							}
						}
					}

					if (element != null && FolderName != element.Name)
					{
						if (element.Name.ToLower() != FolderName.ToLower())
						{
							scriptdata.AddError("Inconsistent FolderName (" + element.Name + "): " + path);
						}
						else
						{
							scriptdata.AddError("Capitalization FolderName (" + element.Name + "): " + path);
						}
					}
					ElementsBackup[FolderName] = element;
				}

				foreach (var dupl in dictDuplIDs)
				{
					scriptdata.AddError(string.Format("Duplicate Entries in Backup Elements folder for ID {0}:{1}", dupl.Key, string.Join(", ", dupl.Value)));
				}
			}

			private void GetServicesMain(ScriptData scriptdata)
			{
				ServiceSync = false;
				string ServiceFolderPath = basePathMain + @"\Services";
				string[] ServicePathsMain = Directory.GetDirectories(ServiceFolderPath);
				ServicesMain = new Dictionary<string, ServiceDetails>();
				Dictionary<string, string> dictIDs = new Dictionary<string, string>();
				Dictionary<string, string[]> dictDuplIDs = new Dictionary<string, string[]>();
				foreach (var path in ServicePathsMain)
				{
					string FolderName = path.Replace(ServiceFolderPath + @"\", string.Empty);
					var service = ServiceDetails.GetServiceDetails(scriptdata, path + @"\Service.xml");
					if (service != null)
					{
						string sID = service.DMAID + "/" + service.ID;
						if (!dictIDs.ContainsKey(sID))
						{
							dictIDs[sID] = FolderName;
						}
						else
						{
							if (dictDuplIDs.ContainsKey(sID))
							{
								string[] asDupFolders = new string[dictDuplIDs[sID].Length + 1];
								for (int i = 0; i < asDupFolders.Length - 1; i++)
								{
									asDupFolders[i] = dictDuplIDs[sID][i];
								}
								asDupFolders[asDupFolders.Length - 1] = FolderName;
								dictDuplIDs[sID] = asDupFolders;
							}
							else
							{
								dictDuplIDs[sID] = new string[] { FolderName, dictIDs[sID] };
							}
						}
					}

					if (service != null && FolderName != service.Name)
					{
						if (service.Name.ToLower() != FolderName.ToLower())
						{
							scriptdata.AddError("Inconsistent FolderName (" + service.Name + "): " + path);
						}
						else
						{
							scriptdata.AddError("Capitalization FolderName (" + service.Name + "): " + path);
						}
					}
					ServicesMain[FolderName] = service;
				}

				foreach (var dupl in dictDuplIDs)
				{
					scriptdata.AddError(string.Format("Duplicate Entries in Main Service folder for ID {0}:{1}", dupl.Key, string.Join(", ", dupl.Value)));
				}
			}

			private void GetServicesBackup(ScriptData scriptdata)
			{
				ServiceSync = false;
				string ServiceFolderPath = basePathBackup + @"\Services";
				string[] ServicePathsBackup = Directory.GetDirectories(ServiceFolderPath);
				ServicesBackup = new Dictionary<string, ServiceDetails>();
				Dictionary<string, string> dictIDs = new Dictionary<string, string>();
				Dictionary<string, string[]> dictDuplIDs = new Dictionary<string, string[]>();
				foreach (var path in ServicePathsBackup)
				{
					string FolderName = path.Replace(ServiceFolderPath + @"\", string.Empty);
					var service = ServiceDetails.GetServiceDetails(scriptdata, path + @"\Service.xml");
					if (service != null)
					{
						string sID = service.DMAID + "/" + service.ID;
						if (!dictIDs.ContainsKey(sID))
						{
							dictIDs[sID] = FolderName;
						}
						else
						{
							if (dictDuplIDs.ContainsKey(sID))
							{
								string[] asDupFolders = new string[dictDuplIDs[sID].Length + 1];
								for (int i = 0; i < asDupFolders.Length - 1; i++)
								{
									asDupFolders[i] = dictDuplIDs[sID][i];
								}
								asDupFolders[asDupFolders.Length - 1] = FolderName;
								dictDuplIDs[sID] = asDupFolders;
							}
							else
							{
								dictDuplIDs[sID] = new string[] { FolderName, dictIDs[sID] };
							}
						}
					}

					if (service != null && FolderName != service.Name)
					{
						if (service.Name.ToLower() != FolderName.ToLower())
						{
							scriptdata.AddError("Inconsistent FolderName (" + service.Name + "): " + path);
						}
						else
						{
							scriptdata.AddError("Capitalization FolderName (" + service.Name + "): " + path);
						}
					}
					ServicesBackup[FolderName] = service;
				}

				foreach (var dupl in dictDuplIDs)
				{
					scriptdata.AddError(string.Format("Duplicate Entries in Backup Service folder for ID {0}:{1}", dupl.Key, string.Join(", ", dupl.Value)));
				}
			}

			private void GetRemoteServicesMain(ScriptData scriptdata)
			{
				string RemoteServiceFolderPath = basePathMain + @"\RemoteServices";
				string[] asRemDMAs = Directory.GetDirectories(RemoteServiceFolderPath);
				dictRemoteServicesMain = new Dictionary<int, Dictionary<string, ServiceDetails>>();
				foreach (var RemDMA in asRemDMAs)
				{
					int iRemDMA;
					if (!Int32.TryParse(RemDMA.Replace(RemoteServiceFolderPath + @"\", string.Empty), out iRemDMA))
					{
						scriptdata.AddError("Failed to retrieve RemDMAID for (" + DMAID + "): " + RemDMA);
						continue;
					}

					Dictionary<string, ServiceDetails> dictRemServs = new Dictionary<string, ServiceDetails>();
					string[] asRemDMAServices = Directory.GetDirectories(RemDMA);
					foreach (var servPath in asRemDMAServices)
					{
						string FolderName = servPath.Replace(RemDMA + @"\", string.Empty);
						var service = ServiceDetails.GetServiceDetails(scriptdata, servPath + @"\Service.xml");
						if (service != null && FolderName != service.Name)
						{
							if (service.Name.ToLower() != FolderName.ToLower())
							{
								scriptdata.AddError("Inconsistent FolderName (" + service.Name + "): " + servPath);
							}
							else
							{
								scriptdata.AddError("Capitalization FolderName (" + service.Name + "): " + servPath);
							}
						}
						dictRemServs[FolderName] = service;
					}
					dictRemoteServicesMain[iRemDMA] = dictRemServs;
				}
			}

			private void GetRemoteServicesBackup(ScriptData scriptdata)
			{
				string RemoteServiceFolderPath = basePathBackup + @"\RemoteServices";
				string[] asRemDMAs = Directory.GetDirectories(RemoteServiceFolderPath);
				dictRemoteServicesBackup = new Dictionary<int, Dictionary<string, ServiceDetails>>();
				foreach (var RemDMA in asRemDMAs)
				{
					int iRemDMA;
					if (!Int32.TryParse(RemDMA.Replace(RemoteServiceFolderPath + @"\", string.Empty), out iRemDMA))
					{
						scriptdata.AddError("Failed to retrieve RemDMAID for (" + DMAID + "): " + RemDMA);
						continue;
					}

					Dictionary<string, ServiceDetails> dictRemServs = new Dictionary<string, ServiceDetails>();
					string[] asRemDMAServices = Directory.GetDirectories(RemDMA);
					foreach (var servPath in asRemDMAServices)
					{
						string FolderName = servPath.Replace(RemDMA + @"\", string.Empty);
						var service = ServiceDetails.GetServiceDetails(scriptdata, servPath + @"\Service.xml");
						if (service != null && FolderName != service.Name)
						{
							if (service.Name.ToLower() != FolderName.ToLower())
							{
								scriptdata.AddError("Inconsistent FolderName (" + service.Name + "): " + servPath);
							}
							else
							{
								scriptdata.AddError("Capitalization FolderName (" + service.Name + "): " + servPath);
							}
						}
						dictRemServs[FolderName] = service;
					}
					dictRemoteServicesBackup[iRemDMA] = dictRemServs;
				}
			}

			private void GetSyncFilesMain(ScriptData scriptdata)
			{
				ElementSync = false;
				string SyncFolderPath = basePathMain + @"\Files\SyncInfo";
				dictSyncEntriesMain = SyncInfoDetails.GetSyncDetails(scriptdata, SyncFolderPath + @"\{DO_NOT_REMOVE_C0E05277-A7C5-4969-904D-E2E52076400A}.xml");
			}

			private void GetSyncFilesBackup(ScriptData scriptdata)
			{
				ElementSync = false;
				string SyncFolderPath = basePathBackup + @"\Files\SyncInfo";
				dictSyncEntriesBackup = SyncInfoDetails.GetSyncDetails(scriptdata, SyncFolderPath + @"\{DO_NOT_REMOVE_C0E05277-A7C5-4969-904D-E2E52076400A}.xml");
			}
			#endregion

			#region CompareDMApair
			private void CompareElementsDMAs(ScriptData scriptdata)
			{

				List<string> MissinOnMain = new List<string>();
				List<string> FailedToParse = new List<string>();
				List<string> MissinOnBackup = new List<string>();
				List<string> NotInSync = new List<string>();
				foreach (var element in ElementsMain)
				{
					if (!ElementsBackup.ContainsKey(element.Key))
					{
						MissinOnBackup.Add(element.Key);
					}
					else if (element.Value == null || ElementsBackup[element.Key] == null)
					{
						FailedToParse.Add(element.Key);
					}
					else
					{
						string sLog;
						if (!element.Value.Equals(ElementsBackup[element.Key], out sLog))
						{
							NotInSync.Add(element.Key + ": " + sLog ?? "<Null>");
						}
					}
				}
				foreach (var element in ElementsBackup)
				{
					if (!ElementsMain.ContainsKey(element.Key))
					{
						MissinOnMain.Add(element.Key);
					}
				}


				StringBuilder sb = new StringBuilder();
				string pairSuffix = string.IsNullOrEmpty(FailoverName) ? " '" + ComputerName + "'" : " (FO Pair: '" + ComputerName + "' <-> '" + FailoverName + "')";
				sb.Append(ScriptData.GetHeader("ELEMENT FOLDER INFO FOR DMA " + DMAID + pairSuffix)).Append(Environment.NewLine);
				sb.Append("INFO: Compared based on DMAID, ELID, Name, Protocol, Version and Properties").Append(Environment.NewLine);
				if (MissinOnMain.Count() > 0 || FailedToParse.Count() > 0 || MissinOnBackup.Count() > 0 || NotInSync.Count() > 0)
				{
					if (MissinOnMain.Count() > 0)
					{
						sb.Append("------ Missing on '" + ComputerName + "' => " + IP + " (Main) ------").Append(Environment.NewLine);
						sb.Append(string.Join(Environment.NewLine, MissinOnMain)).Append(Environment.NewLine);
					}

					if (MissinOnBackup.Count() > 0)
					{
						sb.Append("------ Missing on '" + FailoverName + "' => " + FailoverIP + " (Backup) ------").Append(Environment.NewLine);
						sb.Append(string.Join(Environment.NewLine, MissinOnBackup)).Append(Environment.NewLine);
					}
					if (NotInSync.Count() > 0)
					{
						sb.Append("------ Not in Sync (on both) ------").Append(Environment.NewLine);
						sb.Append(string.Join(Environment.NewLine, NotInSync)).Append(Environment.NewLine);
					}
					if (FailedToParse.Count() > 0)
					{
						sb.Append("------ Failed to parse (on both) ------").Append(Environment.NewLine);
						sb.Append(string.Join(Environment.NewLine, FailedToParse)).Append(Environment.NewLine);
					}
				}
				else
				{
					sb.Append(IP + " And " + FailoverIP + " Are In Sync!").Append(Environment.NewLine);
					ElementSync = true;
				}

				sb.Append(ScriptData.GetTrailer());
				scriptdata.AddDebugCompleetPart(sb.ToString());
			}

			private void CompareServicesDMAs(ScriptData scriptdata)
			{
				List<string> MissinOnMain = new List<string>();
				List<string> FailedToParse = new List<string>();
				List<string> MissinOnBackup = new List<string>();
				List<string> NotInSync = new List<string>();
				foreach (var service in ServicesMain)
				{
					if (!ServicesBackup.ContainsKey(service.Key))
					{
						MissinOnBackup.Add(service.Key);
					}
					else if (service.Value == null || ServicesBackup[service.Key] == null)
					{
						FailedToParse.Add(service.Key);
					}
					else
					{
						string sLog;
						if (!service.Value.Equals(ServicesBackup[service.Key], out sLog))
						{
							NotInSync.Add(service.Key + ": " + sLog ?? "<Null>");
						}
					}
				}
				foreach (var service in ServicesBackup)
				{
					if (!ServicesMain.ContainsKey(service.Key))
					{
						MissinOnMain.Add(service.Key);
					}
				}


				StringBuilder sb = new StringBuilder();
				string pairSuffix = string.IsNullOrEmpty(FailoverName) ? " '" + ComputerName + "'" : " (FO Pair: '" + ComputerName + "' <-> '" + FailoverName + "')";
				sb.Append(ScriptData.GetHeader("Service FOLDER INFO FOR DMA " + DMAID + pairSuffix)).Append(Environment.NewLine);
				sb.Append("INFO: Compared based on DMAID, ID, Name and Properties").Append(Environment.NewLine);

				if (MissinOnMain.Count() > 0 || FailedToParse.Count() > 0 || MissinOnBackup.Count() > 0 || NotInSync.Count() > 0)
				{
					if (MissinOnMain.Count() > 0)
					{
						sb.Append("------ Missing on '" + ComputerName + "' => " + IP + " (Main) ------").Append(Environment.NewLine);
						sb.Append(string.Join(Environment.NewLine, MissinOnMain)).Append(Environment.NewLine);
					}

					if (MissinOnBackup.Count() > 0)
					{
						sb.Append("------ Missing on '" + FailoverName + "' => " + FailoverIP + " (Backup) ------").Append(Environment.NewLine);
						sb.Append(string.Join(Environment.NewLine, MissinOnBackup)).Append(Environment.NewLine);
					}
					if (NotInSync.Count() > 0)
					{
						sb.Append("------ Not in Sync (on both) ------").Append(Environment.NewLine);
						sb.Append(string.Join(Environment.NewLine, NotInSync)).Append(Environment.NewLine);
					}
					if (FailedToParse.Count() > 0)
					{
						sb.Append("------ Failed to parse (on both) ------").Append(Environment.NewLine);
						sb.Append(string.Join(Environment.NewLine, FailedToParse)).Append(Environment.NewLine);
					}
				}
				else
				{
					sb.Append(IP + " And " + FailoverIP + " Are In Sync!").Append(Environment.NewLine);
					ServiceSync = true;
				}
				sb.Append(ScriptData.GetTrailer());
				scriptdata.AddDebugCompleetPart(sb.ToString());
			}

			private void CompareElementsWithSLNet(ScriptData scriptdata, Dictionary<string, ElementHelper.ScriptElement> allElements)
			{
				//Compare with Main (this will only be checked if folders are in sync)
				List<string> lNotInSLNet = new List<string>();
				List<string> lNotInFolder = new List<string>();
				List<string> lNotInSync = new List<string>();

				foreach (var SLNetEl in allElements.Values)
				{
					if (SLNetEl.iHostedDMAID != DMAID)
					{ continue; }

					if (SLNetEl.DVE)//For now DVE's are ignored
					{ continue; }

					if (ElementsMain.ContainsKey(SLNetEl.Name))
					{
						string sLog;
						if (!ElementsMain[SLNetEl.Name].Equals(SLNetEl, out sLog))
						{
							lNotInSync.Add(SLNetEl.Name + ": " + sLog ?? "<Null>");
						}
					}
					else //LOOK BY ID
					{
						bool found = false;
						foreach (var FolderEl in ElementsMain)
						{
							if (FolderEl.Value.DMAID == SLNetEl.iDataMinerID && FolderEl.Value.ELID == SLNetEl.iElementID)
							{
								string sLog;
								if (!FolderEl.Value.Equals(SLNetEl, out sLog))
								{
									lNotInSync.Add(SLNetEl.Name + ": " + sLog ?? "<Null>");
								}
								found = true;
								break;
							}
						}
						if (!found)
						{
							lNotInFolder.Add(SLNetEl.Name);
						}
					}
				}
				foreach (var FolderEl in ElementsMain)
				{
					if (allElements.ContainsKey(FolderEl.Value.DMAID + "/" + FolderEl.Value.ELID))
					{
						continue;
					}
					lNotInSLNet.Add(FolderEl.Key);
				}

				StringBuilder sb = new StringBuilder();
				sb.Append(ScriptData.GetHeader("Elements INFO FOR DMA " + DMAID + " '" + ComputerName + "'")).Append(Environment.NewLine);
				sb.Append("INFO: Compared based on DMAID, ID, protocol, version and Name").Append(Environment.NewLine);

				if (lNotInSLNet.Count() > 0 || lNotInFolder.Count() > 0 || lNotInSync.Count() > 0)
				{
					if (lNotInFolder.Count() > 0)
					{
						sb.Append("Missing in Folder: ").Append(Environment.NewLine);
						sb.Append(string.Join(Environment.NewLine, lNotInFolder)).Append(Environment.NewLine);
					}
					if (lNotInSLNet.Count() > 0)
					{
						sb.Append("Missing in SLNet: ").Append(Environment.NewLine);
						sb.Append(string.Join(Environment.NewLine, lNotInSLNet)).Append(Environment.NewLine);
					}
					if (lNotInSync.Count() > 0)
					{
						sb.Append("Not in Sync: ").Append(Environment.NewLine);
						sb.Append(string.Join(Environment.NewLine, lNotInSync)).Append(Environment.NewLine);
					}
				}
				else
				{
					sb.Append("SLNet And Folder Structure Are In Sync!").Append(Environment.NewLine);
					ServiceSync = true;
				}
				sb.Append(ScriptData.GetTrailer());
				scriptdata.AddDebugCompleetPart(sb.ToString());

			}

			private void CompareServiceWithSLNet(ScriptData scriptdata, Dictionary<string, ServiceHelper.ScriptService> allServices)
			{
				//Compare with Main (this will only be checked if folders are in sync)
				List<string> lNotInSLNet = new List<string>();
				List<string> lNotInFolder = new List<string>();
				List<string> lNotInSync = new List<string>();

				foreach (var SLNetServ in allServices.Values)
				{
					if (SLNetServ.iHostedDMAID != DMAID)
					{
						continue;
					}

					if (ServicesMain.ContainsKey(SLNetServ.Name))
					{
						string sLog;
						if (!ServicesMain[SLNetServ.Name].Equals(SLNetServ, out sLog))
						{
							lNotInSync.Add(SLNetServ.Name + ": " + sLog ?? "<Null>");
						}
					}
					else //LOOK BY ID
					{
						bool found = false;
						foreach (var FolderServ in ServicesMain)
						{
							if (FolderServ.Value.DMAID == SLNetServ.iDataMinerID && FolderServ.Value.ID == SLNetServ.iServiceID)
							{
								string sLog;
								if (!FolderServ.Value.Equals(SLNetServ, out sLog))
								{
									lNotInSync.Add(SLNetServ.Name + ": " + sLog ?? "<Null>");
								}
								found = true;
								break;
							}
						}
						if (!found)
						{
							lNotInFolder.Add(SLNetServ.Name);
						}
					}
				}
				foreach (var FolderServ in ServicesMain)
				{
					if (allServices.ContainsKey(FolderServ.Value.DMAID + "/" + FolderServ.Value.ID))
					{
						continue;
					}
					lNotInSLNet.Add(FolderServ.Key);
				}

				StringBuilder sb = new StringBuilder();
				sb.Append(ScriptData.GetHeader("Service INFO FOR DMA " + DMAID + " '" + ComputerName + "'")).Append(Environment.NewLine);
				sb.Append("INFO: Compared based on DMAID, ID, protocol, version and Name").Append(Environment.NewLine);

				if (lNotInSLNet.Count() > 0 || lNotInFolder.Count() > 0 || lNotInSync.Count() > 0)
				{
					if (lNotInFolder.Count() > 0)
					{
						sb.Append("Missing in Folder: ").Append(Environment.NewLine);
						sb.Append(string.Join(Environment.NewLine, lNotInFolder)).Append(Environment.NewLine);
					}
					if (lNotInSLNet.Count() > 0)
					{
						sb.Append("Missing in SLNet: ").Append(Environment.NewLine);
						sb.Append(string.Join(Environment.NewLine, lNotInSLNet)).Append(Environment.NewLine);
					}
					if (lNotInSync.Count() > 0)
					{
						sb.Append("Not in Sync: ").Append(Environment.NewLine);
						sb.Append(string.Join(Environment.NewLine, lNotInSync)).Append(Environment.NewLine);
					}
				}
				else
				{
					sb.Append("SLNet And Folder Structure Are In Sync!").Append(Environment.NewLine);
					ServiceSync = true;
				}
				sb.Append(ScriptData.GetTrailer());
				scriptdata.AddDebugCompleetPart(sb.ToString());
			}

			private void CompareRemoteServices(ScriptData scriptdata)
			{
				List<string> lMissingOnMain = new List<string>();
				List<string> lFailedToParse = new List<string>();
				List<string> lMissingOnBackup = new List<string>();
				List<string> lMissingDMAOnMain = new List<string>();
				List<string> lMissingDMAOnBackup = new List<string>();
				List<string> lNotInSync = new List<string>();

				foreach (var DMA in dictRemoteServicesBackup)
				{
					if (dictRemoteServicesMain.ContainsKey(DMA.Key))
					{
						foreach (var service in DMA.Value)
						{
							if (!dictRemoteServicesMain[DMA.Key].ContainsKey(service.Key))
							{
								lMissingOnMain.Add(DMA.Key + ": " + service.Key);
							}
							else if (service.Value == null || dictRemoteServicesMain[DMA.Key][service.Key] == null)
							{
								lFailedToParse.Add(DMA.Key + ": " + service.Key);
							}
							else
							{
								string sLog;
								if (!service.Value.Equals(dictRemoteServicesMain[DMA.Key][service.Key], out sLog))
								{
									lNotInSync.Add(DMA.Key + ": " + service.Key + ": " + sLog ?? "<Null>");
								}
							}
						}
					}
					else
					{
						lMissingDMAOnMain.Add(DMA.Key.ToString());
					}
				}
				foreach (var DMA in dictRemoteServicesMain)
				{
					if (dictRemoteServicesBackup.ContainsKey(DMA.Key))
					{
						foreach (var service in DMA.Value)
						{
							if (!dictRemoteServicesBackup[DMA.Key].ContainsKey(service.Key))
							{
								lMissingOnBackup.Add(DMA.Key + ": " + service.Key);
							}
						}
					}
					else
					{
						lMissingDMAOnBackup.Add(DMA.Key.ToString());
					}
				}

				StringBuilder sb = new StringBuilder();
				string pairSuffix = string.IsNullOrEmpty(FailoverName) ? " '" + ComputerName + "'" : " (FO Pair: '" + ComputerName + "' <-> '" + FailoverName + "')";
				sb.Append(ScriptData.GetHeader("Remote Service Folder Check for DMA " + DMAID + pairSuffix)).Append(Environment.NewLine);
				sb.Append("INFO: MAKE SURE MAIN AND BACKUP ARE IN SYNC FIRST!").Append(Environment.NewLine);

				if (lMissingOnMain.Count() > 0 || lFailedToParse.Count() > 0 || lMissingOnBackup.Count() > 0 || lMissingDMAOnMain.Count() > 0 || lMissingDMAOnBackup.Count() > 0 || lNotInSync.Count() > 0)
				{
					if (lMissingDMAOnMain.Count() > 0 || lMissingOnMain.Count() > 0)
					{
						sb.Append("------ Missing on '" + ComputerName + "' => " + IP + " (Main) ------").Append(Environment.NewLine);
						if (lMissingDMAOnMain.Count() > 0)
						{
							sb.Append("Missing DMA: ").Append(Environment.NewLine);
							sb.Append(string.Join(Environment.NewLine, lMissingDMAOnMain)).Append(Environment.NewLine);
						}

						if (lMissingOnMain.Count() > 0)
						{
							sb.Append("Missing services: ").Append(Environment.NewLine);
							sb.Append(string.Join(Environment.NewLine, lMissingOnMain)).Append(Environment.NewLine);
						}
					}

					if (lMissingDMAOnBackup.Count() > 0 || lMissingOnBackup.Count() > 0)
					{
						sb.Append("------ Missing on '" + FailoverName + "' => " + FailoverIP + " (Backup) ------").Append(Environment.NewLine);
						if (lMissingDMAOnBackup.Count() > 0)
						{
							sb.Append("Missing DMA: ").Append(Environment.NewLine);
							sb.Append(string.Join(Environment.NewLine, lMissingDMAOnBackup)).Append(Environment.NewLine);
						}

						if (lMissingOnBackup.Count() > 0)
						{
							sb.Append("Missing services: ").Append(Environment.NewLine);
							sb.Append(string.Join(Environment.NewLine, lMissingOnBackup)).Append(Environment.NewLine);
						}
					}

					if (lFailedToParse.Count() > 0)
					{
						sb.Append("------ Failed to parse (on both) ------").Append(Environment.NewLine);
						sb.Append(string.Join(Environment.NewLine, lFailedToParse)).Append(Environment.NewLine);
					}

					if (lNotInSync.Count() > 0)
					{
						sb.Append("------ Not in Sync (on both) ------").Append(Environment.NewLine);
						sb.Append(string.Join(Environment.NewLine, lNotInSync)).Append(Environment.NewLine);
					}
				}
				else
				{
					sb.Append("The remote services for DMA " + DMAID + " are in Sync!").Append(Environment.NewLine);
				}
				sb.Append(ScriptData.GetTrailer());
				scriptdata.AddDebugCompleetPart(sb.ToString());
			}
			#endregion

			#region CompareCluster
			public void CompareRemServicesWithDMAs(ScriptData scriptdata, Dictionary<int, Dictionary<string, ServiceDetails>> dictFoundServices)
			{
				List<string> lMissingOnRemFolder = new List<string>();
				List<string> lFailedToParse = new List<string>();
				List<string> lMissingOnDMA = new List<string>();
				List<string> lMissingDMAOnRemFolder = new List<string>();
				List<string> lMissingDMA = new List<string>();
				List<string> lNotInSync = new List<string>();
				bool RemFolderContainsOwn = false;

				foreach (var DMA in dictFoundServices)
				{
					if (dictRemoteServicesMain.ContainsKey(DMA.Key) && DMA.Key != DMAID)
					{
						foreach (var service in DMA.Value)
						{
							if (!dictRemoteServicesMain[DMA.Key].ContainsKey(service.Key))
							{
								lMissingOnRemFolder.Add(DMA.Key + ": " + service.Key);
							}
							else if (service.Value == null || dictRemoteServicesMain[DMA.Key][service.Key] == null)
							{
								lFailedToParse.Add(DMA.Key + ": " + service.Key);
							}
							else
							{
								string sLog;
								if (!service.Value.Equals(dictRemoteServicesMain[DMA.Key][service.Key], out sLog))
								{
									lNotInSync.Add(service.Key + ": " + sLog ?? "<Null>");
								}
							}
						}
					}
					else if (DMA.Key != DMAID && DMA.Value != null && DMA.Value.Count() > 0)
					{
						lMissingDMAOnRemFolder.Add(DMA.Key.ToString());
					}
				}
				foreach (var DMA in dictRemoteServicesMain)
				{
					if (DMA.Key == DMAID)
					{
						RemFolderContainsOwn = true;
					}
					else if (dictFoundServices.ContainsKey(DMA.Key))
					{
						foreach (var service in DMA.Value)
						{
							if (!dictFoundServices[DMA.Key].ContainsKey(service.Key))
							{
								lMissingOnDMA.Add(DMA.Key + ": " + service.Key);
							}
						}
					}
					else
					{
						lMissingDMA.Add(DMA.Key.ToString());
					}
				}

				StringBuilder sb = new StringBuilder();
				sb.Append(ScriptData.GetHeader("Remote Service Folder Check for DMA " + DMAID + " '" + ComputerName + "'" + " with other DMAs")).Append(Environment.NewLine);
				sb.Append("INFO: MAKE SURE MAIN AND BACKUP ARE IN SYNC FIRST!").Append(Environment.NewLine);

				if (RemFolderContainsOwn)
				{
					sb.Append("Remote service folder contains its own DMA ").Append(DMAID).Append(Environment.NewLine);
				}

				if (lMissingOnRemFolder.Count() > 0 || lFailedToParse.Count() > 0 || lMissingOnDMA.Count() > 0 || lMissingDMAOnRemFolder.Count() > 0 || lMissingDMA.Count() > 0 || lNotInSync.Count() > 0)
				{
					if (lMissingDMAOnRemFolder.Count() > 0)
					{
						sb.Append("Missing DMA in remote service folder: ").Append(Environment.NewLine);
						sb.Append(string.Join(Environment.NewLine, lMissingDMAOnRemFolder)).Append(Environment.NewLine);
					}

					if (lMissingDMA.Count() > 0)
					{
						sb.Append("Missing DMA: ").Append(Environment.NewLine);
						sb.Append(string.Join(Environment.NewLine, lMissingDMA)).Append(Environment.NewLine);
					}

					if (lMissingOnRemFolder.Count() > 0)
					{
						sb.Append("Missing services in remote service folder: ").Append(Environment.NewLine);
						sb.Append(string.Join(Environment.NewLine, lMissingOnRemFolder)).Append(Environment.NewLine);
					}

					if (lMissingOnDMA.Count() > 0)
					{
						sb.Append("Missing services: ").Append(Environment.NewLine);
						sb.Append(string.Join(Environment.NewLine, lMissingOnDMA)).Append(Environment.NewLine);
					}

					if (lFailedToParse.Count() > 0)
					{
						sb.Append("Failed to parse: ").Append(Environment.NewLine);
						sb.Append(string.Join(Environment.NewLine, lFailedToParse)).Append(Environment.NewLine);
					}

					if (lNotInSync.Count() > 0)
					{
						sb.Append("Failed to parse: ").Append(Environment.NewLine);
						sb.Append(string.Join(Environment.NewLine, lNotInSync)).Append(Environment.NewLine);
					}
				}
				else
				{
					sb.Append("The remote services for DMA " + DMAID + " are in Sync!").Append(Environment.NewLine);
				}
				sb.Append(ScriptData.GetTrailer());
				scriptdata.AddDebugCompleetPart(sb.ToString());
			}

			public void CompareSyncFileMain(ScriptData scriptdata, Dictionary<int, Dictionary<string, ServiceDetails>> dictFoundServices, Dictionary<int, Dictionary<string, ElementDetails>> dictFoundElements)
			{
				StringBuilder sb = new StringBuilder();
				sb.Append(ScriptData.GetHeader("Sync Info Folder Check for Main DMA " + DMAID + " '" + ComputerName + "' with other DMAs")).Append(Environment.NewLine);
				sb.Append("INFO: Don't make changes to the sync document unless you know how!").Append(Environment.NewLine);

				HashSet<string> hsFilesChecked = new HashSet<string>();

				foreach (var DMAServices in dictFoundServices.Values)
				{
					foreach (var service in DMAServices)
					{
						string sServiceFile = @"C:\Skyline DataMiner\Services\" + service.Key + @"\Service.xml";
						if (dictSyncEntriesMain.ContainsKey(sServiceFile))
						{
							hsFilesChecked.Add(sServiceFile);
						}
						else
						{
							sb.Append("Missing entry from sync file: ").Append(sServiceFile).Append(Environment.NewLine);
						}
					}
				}

				foreach (var DMAElements in dictFoundElements.Values)
				{
					foreach (var element in DMAElements)
					{
						string sElementFile1 = @"C:\Skyline DataMiner\Elements\" + element.Key + @"\Element.xml";
						string sElementFile2 = @"C:\Skyline DataMiner\Elements\" + element.Key + @"\ElementData.xml";
						string sElementFile3 = @"C:\Skyline DataMiner\Elements\" + element.Key + @"\Description.xml";

						if (dictSyncEntriesMain.ContainsKey(sElementFile1))
						{
							hsFilesChecked.Add(sElementFile1);
						}
						else
						{
							sb.Append("Missing entry from sync file: ").Append(sElementFile1).Append(Environment.NewLine);
						}

						if (dictSyncEntriesMain.ContainsKey(sElementFile2))
						{
							hsFilesChecked.Add(sElementFile2);
						}
						else
						{
							sb.Append("Missing entry from sync file: ").Append(sElementFile2).Append(Environment.NewLine);
						}

						if (dictSyncEntriesMain.ContainsKey(sElementFile3))
						{
							hsFilesChecked.Add(sElementFile3);
						}
						else
						{
							sb.Append("Missing entry from sync file: ").Append(sElementFile3).Append(Environment.NewLine);
						}
					}
				}

				int iCorrelationRulesNotChecked = 0;
				int iDocumentsNotChecked = 0;
				int iProtocolsNotChecked = 0;
				int iScriptsNotChecked = 0;
				int iUsersNotChecked = 0;
				int iViewsNotChecked = 0;
				int iWebPagesNotChecked = 0;
				int iOtherNotChecked = 0;
				foreach (var syncFile in dictSyncEntriesMain.Values)
				{
					if (hsFilesChecked.Contains(syncFile.file))
					{
						continue;
					}

					if (syncFile.file.StartsWith(@"C:\Skyline DataMiner\Services\"))
					{
						sb.Append("Missing service: ").Append(syncFile.file).Append(Environment.NewLine);
					}
					else if (syncFile.file.StartsWith(@"C:\Skyline DataMiner\Elements\"))
					{
						sb.Append("Missing element: ").Append(syncFile.file).Append(Environment.NewLine);
					}
					else if (syncFile.file.StartsWith(@"C:\Skyline DataMiner\Correlation\"))
					{
						iCorrelationRulesNotChecked++;
					}
					else if (syncFile.file.StartsWith(@"C:\Skyline DataMiner\Documents\"))
					{
						iDocumentsNotChecked++;
					}
					else if (syncFile.file.StartsWith(@"C:\Skyline DataMiner\Protocols\"))
					{
						iProtocolsNotChecked++;
					}
					else if (syncFile.file.StartsWith(@"C:\Skyline DataMiner\Scripts\"))
					{
						iScriptsNotChecked++;
					}
					else if (syncFile.file.StartsWith(@"C:\Skyline DataMiner\Users\"))
					{
						iUsersNotChecked++;
					}
					else if (syncFile.file.StartsWith(@"C:\Skyline DataMiner\Views\"))
					{
						iViewsNotChecked++;
					}
					else if (syncFile.file.StartsWith(@"C:\Skyline DataMiner\WebPages\"))
					{
						iWebPagesNotChecked++;
					}
					else
					{
						iOtherNotChecked++;
					}
				}
				sb.Append("Not checked Correlation: ").Append(iCorrelationRulesNotChecked).Append(Environment.NewLine);
				sb.Append("Not checked Documents: ").Append(iDocumentsNotChecked).Append(Environment.NewLine);
				sb.Append("Not checked Protocols: ").Append(iProtocolsNotChecked).Append(Environment.NewLine);
				sb.Append("Not checked Scripts: ").Append(iScriptsNotChecked).Append(Environment.NewLine);
				sb.Append("Not checked Users: ").Append(iUsersNotChecked).Append(Environment.NewLine);
				sb.Append("Not checked Views: ").Append(iViewsNotChecked).Append(Environment.NewLine);
				sb.Append("Not checked WebPages: ").Append(iWebPagesNotChecked).Append(Environment.NewLine);
				sb.Append("Not checked Other: ").Append(iOtherNotChecked).Append(Environment.NewLine);
				sb.Append(ScriptData.GetTrailer());
				scriptdata.AddDebugCompleetPart(sb.ToString());
			}

			public void CompareSyncFileBackup(ScriptData scriptdata, Dictionary<int, Dictionary<string, ServiceDetails>> dictFoundServices, Dictionary<int, Dictionary<string, ElementDetails>> dictFoundElements)
			{
				StringBuilder sb = new StringBuilder();
				sb.Append(ScriptData.GetHeader("Sync Info Folder Check for Backup DMA " + DMAID + " '" + FailoverName + "' with other DMAs")).Append(Environment.NewLine);
				sb.Append("INFO: Don't make changes to the sync document unless you know how!").Append(Environment.NewLine);

				HashSet<string> hsFilesChecked = new HashSet<string>();

				foreach (var DMAServices in dictFoundServices.Values)
				{
					foreach (var service in DMAServices)
					{
						string sServiceFile = @"C:\Skyline DataMiner\Services\" + service.Key + @"\Service.xml";
						if (dictSyncEntriesBackup.ContainsKey(sServiceFile))
						{
							hsFilesChecked.Add(sServiceFile);
						}
						else
						{
							sb.Append("Missing entry from sync file: ").Append(sServiceFile).Append(Environment.NewLine);
						}
					}
				}

				foreach (var DMAElements in dictFoundElements.Values)
				{
					foreach (var element in DMAElements)
					{
						string sElementFile1 = @"C:\Skyline DataMiner\Elements\" + element.Key + @"\Element.xml";
						string sElementFile2 = @"C:\Skyline DataMiner\Elements\" + element.Key + @"\ElementData.xml";
						string sElementFile3 = @"C:\Skyline DataMiner\Elements\" + element.Key + @"\Description.xml";

						if (dictSyncEntriesBackup.ContainsKey(sElementFile1))
						{
							hsFilesChecked.Add(sElementFile1);
						}
						else
						{
							sb.Append("Missing entry from sync file: ").Append(sElementFile1).Append(Environment.NewLine);
						}

						if (dictSyncEntriesBackup.ContainsKey(sElementFile2))
						{
							hsFilesChecked.Add(sElementFile2);
						}
						else
						{
							sb.Append("Missing entry from sync file: ").Append(sElementFile2).Append(Environment.NewLine);
						}

						if (dictSyncEntriesBackup.ContainsKey(sElementFile3))
						{
							hsFilesChecked.Add(sElementFile3);
						}
						else
						{
							sb.Append("Missing entry from sync file: ").Append(sElementFile3).Append(Environment.NewLine);
						}
					}
				}

				int iCorrelationRulesNotChecked = 0;
				int iDocumentsNotChecked = 0;
				int iProtocolsNotChecked = 0;
				int iScriptsNotChecked = 0;
				int iUsersNotChecked = 0;
				int iViewsNotChecked = 0;
				int iWebPagesNotChecked = 0;
				int iOtherNotChecked = 0;
				foreach (var syncFile in dictSyncEntriesBackup.Values)
				{
					if (hsFilesChecked.Contains(syncFile.file))
					{
						continue;
					}

					if (syncFile.file.StartsWith(@"C:\Skyline DataMiner\Services\"))
					{
						sb.Append("Missing service: ").Append(syncFile.file).Append(Environment.NewLine);
					}
					else if (syncFile.file.StartsWith(@"C:\Skyline DataMiner\Elements\"))
					{
						sb.Append("Missing element: ").Append(syncFile.file).Append(Environment.NewLine);
					}
					else if (syncFile.file.StartsWith(@"C:\Skyline DataMiner\Correlation\"))
					{
						iCorrelationRulesNotChecked++;
					}
					else if (syncFile.file.StartsWith(@"C:\Skyline DataMiner\Documents\"))
					{
						iDocumentsNotChecked++;
					}
					else if (syncFile.file.StartsWith(@"C:\Skyline DataMiner\Protocols\"))
					{
						iProtocolsNotChecked++;
					}
					else if (syncFile.file.StartsWith(@"C:\Skyline DataMiner\Scripts\"))
					{
						iScriptsNotChecked++;
					}
					else if (syncFile.file.StartsWith(@"C:\Skyline DataMiner\Users\"))
					{
						iUsersNotChecked++;
					}
					else if (syncFile.file.StartsWith(@"C:\Skyline DataMiner\Views\"))
					{
						iViewsNotChecked++;
					}
					else if (syncFile.file.StartsWith(@"C:\Skyline DataMiner\WebPages\"))
					{
						iWebPagesNotChecked++;
					}
					else
					{
						iOtherNotChecked++;
					}
				}
				sb.Append("Not checked Correlation: ").Append(iCorrelationRulesNotChecked).Append(Environment.NewLine);
				sb.Append("Not checked Documents: ").Append(iDocumentsNotChecked).Append(Environment.NewLine);
				sb.Append("Not checked Protocols: ").Append(iProtocolsNotChecked).Append(Environment.NewLine);
				sb.Append("Not checked Scripts: ").Append(iScriptsNotChecked).Append(Environment.NewLine);
				sb.Append("Not checked Users: ").Append(iUsersNotChecked).Append(Environment.NewLine);
				sb.Append("Not checked Views: ").Append(iViewsNotChecked).Append(Environment.NewLine);
				sb.Append("Not checked WebPages: ").Append(iWebPagesNotChecked).Append(Environment.NewLine);
				sb.Append("Not checked Other: ").Append(iOtherNotChecked).Append(Environment.NewLine);
				sb.Append(ScriptData.GetTrailer());
				scriptdata.AddDebugCompleetPart(sb.ToString());
			}
			#endregion

			public void RetrieveInfoFromFolders(ScriptData scriptdata, Dictionary<string, ElementHelper.ScriptElement> dictAllElements, Dictionary<string, ServiceHelper.ScriptService> dictAllServices)
			{
				try
				{
					#region Main
					basePathMain = (IP == null) ? @"C:\Skyline DataMiner" : @"\\" + IP + @"\c$\Skyline DataMiner";

					if (IP == null)

					{
						scriptdata.RegisterDMA("MAIN", null, ComputerName);
						GetElementsMain(scriptdata);
						GetServicesMain(scriptdata);
						GetRemoteServicesMain(scriptdata);
						if (SyncInfo)
						{
							GetSyncFilesMain(scriptdata);
						}
					}
					else
					{
						string MainDomain = domain;
						if (domain == "localuser" || domain == "" || domain == null)
						{
							try
							{
								MainDomain = Dns.GetHostEntry(IP).HostName;
							}
							catch (System.Net.Sockets.SocketException)
							{
								MainDomain = ComputerName;
							}
						}
						scriptdata.AddDebug("Connecting to MAIN: " + IP + " as " + MainDomain + @"\" + username);
						scriptdata.RegisterDMA("MAIN", IP, ComputerName);
						using (NetworkShareAccesser.Access(IP, MainDomain, username, password))
						{
							GetElementsMain(scriptdata);
							GetServicesMain(scriptdata);
							GetRemoteServicesMain(scriptdata);
							if (SyncInfo)
							{
								GetSyncFilesMain(scriptdata);
							}
						}
					}
					#endregion

					#region Failover
					if (FailoverIP != null)
					{
						basePathBackup = @"\\" + FailoverIP + @"\c$\Skyline DataMiner";

						string FailoverDomain = domain;
						if (domain == "localuser" || domain == "" || domain == null)
						{
							try
							{
								FailoverDomain = Dns.GetHostEntry(FailoverIP).HostName;
							}
							catch (System.Net.Sockets.SocketException)
							{
								FailoverDomain = FailoverName;
							}
						}
						scriptdata.AddDebug("Connecting to BACKUP: " + FailoverIP + " as " + FailoverDomain + @"\" + username);
						scriptdata.RegisterDMA("BACKUP", FailoverIP, FailoverName);
						using (NetworkShareAccesser.Access(FailoverIP, FailoverDomain, username, password))
						{
							GetElementsBackup(scriptdata);
							GetServicesBackup(scriptdata);
							GetRemoteServicesBackup(scriptdata);
							if (SyncInfo)
							{
								GetSyncFilesBackup(scriptdata);
							}
						}
					}
					#endregion

					#region Compare
					if (ElementsMain != null && ElementsBackup != null)
					{
						CompareElementsDMAs(scriptdata);
					}
					if (ServicesMain != null && ServicesBackup != null)
					{
						CompareServicesDMAs(scriptdata);
					}
					if (dictRemoteServicesMain != null && dictRemoteServicesBackup != null)
					{
						CompareRemoteServices(scriptdata);
					}
					if ((ElementSync && ServiceSync) || FailoverIP == null)
					{
						CompareElementsWithSLNet(scriptdata, dictAllElements);
						CompareServiceWithSLNet(scriptdata, dictAllServices);
					}
					#endregion
				}
				catch (Exception e)
				{
					scriptdata.AddError("RetrieveInfoFromFolders Exception -> " + e.GetType().Name + ": " + e.Message);
				}
			}

			#region Classes
			public class ServiceDetails
			{
				public int DMAID { get; set; }
				public int ID { get; set; }
				public string Name { get; set; }
				public List<PropertyDetails> Props { get; set; }

				private ServiceDetails(XDocument xDoc)
				{
					var ns = xDoc.Root.GetDefaultNamespace();

					var Service = xDoc.Element(ns + "Service");

					DMAID = Convert.ToInt32(Service.Attribute("dmaid").Value);
					ID = Convert.ToInt32(Service.Attribute("id").Value);
					Name = Service.Attribute("name").Value;
					Props = new List<PropertyDetails>();
					var properties = Service.Element(ns + "Properties");
					if (properties != null)
					{
						var props = properties.Elements(ns + "Property");
						if (props != null)
						{
							foreach (var prop in props)
							{
								if (prop.Attribute("name") != null && prop.Attribute("value") != null)
								{
									Props.Add(new PropertyDetails(prop.Attribute("name").Value, prop.Attribute("value").Value));
								}
							}
						}
					}
				}

				private ServiceDetails(Int32 iDMAID, Int32 iID, string sName, List<PropertyDetails> lProps)
				{
					DMAID = iDMAID;
					ID = iID;
					Name = sName;
					Props = lProps;
				}

				public static ServiceDetails GetServiceDetails(ScriptData scriptData, string sPath)
				{
					try
					{
						XDocument xDoc = XDocument.Load(sPath);
						return new ServiceDetails(xDoc);
					}
					catch (Exception e)
					{
						scriptData.AddError("Failed to parse ServiceDetails for " + sPath + " -> " + e.GetType().Name + ": " + e.Message);
						return null;
					}
				}
				public static ServiceDetails GetServiceDetails2(ScriptData scriptData, string sPath)
				{
					try
					{
						int iDMAID = 0;
						int iID = 0;
						string sName = null;
						List<PropertyDetails> lProps = new List<PropertyDetails>();

						using (XmlReader reader = XmlReader.Create(sPath))
						{
							if (!reader.IsStartElement("Service"))
							{
								scriptData.AddError("Failed to start reading service: " + sPath);
							}

							iDMAID = Convert.ToInt32(reader.GetAttribute("dmaid"));
							iID = Convert.ToInt32(reader.GetAttribute("id"));
							sName = reader.GetAttribute("name");
							if (reader.ReadToDescendant("Properties"))
							{
								using (var subtree = reader.ReadSubtree())
								{
									while (subtree.Read())
									{
										if (subtree.NodeType == XmlNodeType.Element && subtree.Name == "Property")
										{
											lProps.Add(new PropertyDetails(subtree.GetAttribute("name"), subtree.GetAttribute("value")));
										}
									}
								}
							}
						}

						return new ServiceDetails(iDMAID, iID, sName, lProps);
					}
					catch (Exception e)
					{
						scriptData.AddError("Failed to parse ServiceDetails for " + sPath + " -> " + e.GetType().Name + ": " + e.Message);
						return null;
					}
				}
				public bool Equals(ServiceDetails compare, out string Logging)
				{
					StringBuilder sb = new StringBuilder();
					bool equal = true;

					if (DMAID != compare.DMAID || ID != compare.ID)
					{
						sb.Append("DMA/ID|");
						equal = false;
					}
					if (Name != compare.Name)
					{
						sb.Append("Name|");
						equal = false;
					}
					var comp = new MultiSetComparer<PropertyDetails>();
					if (Props.Count() != 0 || Props.Count() != 0)
					{
						if (comp.Equals(Props, compare.Props))
						{
							sb.Append("Properties");
							equal = false;
						}
					}

					Logging = sb.ToString();
					return equal;
				}
				public bool Equals(ServiceHelper.ScriptService compare, out string Logging)
				{
					StringBuilder sb = new StringBuilder();
					bool equal = true;

					if (DMAID != compare.iDataMinerID || ID != compare.iServiceID)
					{
						sb.Append("DMA/ID|");
						equal = false;
					}
					if (Name != compare.Name)
					{
						sb.Append("Name|");
						equal = false;
					}

					Logging = sb.ToString();
					return equal;
				}
			}

			public class ElementDetails
			{
				public int DMAID { get; set; }
				public int ELID { get; set; }
				public string Name { get; set; }
				public string Protocol { get; set; }
				public string Version { get; set; }
				public List<PropertyDetails> Props { get; set; }

				private ElementDetails(XDocument xDoc)
				{
					var ns = xDoc.Root.GetDefaultNamespace();

					var Element = xDoc.Element(ns + "Element");

					DMAID = Convert.ToInt32(Element.Attribute("dataMinerId").Value);
					ELID = Convert.ToInt32(Element.Attribute("id").Value);
					Name = Element.Element(ns + "Name").Value;
					Protocol = Element.Element(ns + "Protocol").Element(ns + "Name").Value;
					Version = Element.Element(ns + "Protocol").Element(ns + "Version").Value;
					Props = new List<PropertyDetails>();
					var props = Element.Element(ns + "Properties").Elements(ns + "Property");
					foreach (var prop in props)
					{
						if (prop.Attribute("name") != null && prop.Attribute("value") != null)
						{
							Props.Add(new PropertyDetails(prop.Attribute("name").Value, prop.Attribute("value").Value));
						}
					}
				}

				public static ElementDetails GetElementDetails(ScriptData scriptData, string sPath)
				{
					try
					{
						XDocument xDoc = XDocument.Load(sPath);
						return new ElementDetails(xDoc);
					}
					catch (Exception e)
					{
						scriptData.AddError("Failed to parse ElementDetails for " + sPath + " -> " + e.GetType().Name + ": " + e.Message);
						return null;
					}
				}

				public bool Equals(ElementDetails compare, out string Logging)
				{
					StringBuilder sb = new StringBuilder();
					bool equal = true;

					if (DMAID != compare.DMAID || ELID != compare.ELID)
					{
						sb.Append("DMA/ELID|");
						equal = false;
					}
					if (Name != compare.Name)
					{
						sb.Append("Name|");
						equal = false;
					}
					if (Protocol != compare.Protocol || Version != compare.Version)
					{
						sb.Append("Protocol/Version|");
						equal = false;
					}
					var comp = new MultiSetComparer<PropertyDetails>();
					if (Props.Count() != 0 || Props.Count() != 0)
					{
						if (comp.Equals(Props, compare.Props))
						{
							sb.Append("Properties");
							equal = false;
						}
					}
					Logging = sb.ToString();
					return equal;
				}
				public bool Equals(ElementHelper.ScriptElement compare, out string Logging)
				{
					//TODO INCLUDE PROPERTIES
					StringBuilder sb = new StringBuilder();
					bool equal = true;

					if (DMAID != compare.iDataMinerID || ELID != compare.iElementID)
					{
						sb.Append("DMA/ELID|");
						equal = false;
					}
					if (Name != compare.Name)
					{
						sb.Append("Name|");
						equal = false;
					}
					if (Protocol != compare.ProtocolName || Version != compare.Version)
					{
						sb.Append("Protocol/Version");
						equal = false;
					}
					Logging = sb.ToString();
					return equal;
				}
			}

			public class PropertyDetails : IEquatable<PropertyDetails>
			{
				public String Name { get; set; }
				public String Value { get; set; }
				public PropertyDetails(string sName, string sValue)
				{
					Name = sName;
					Value = sValue;
				}

				public bool Equals(PropertyDetails compare)
				{
					if (Name == compare.Name && Value == compare.Value)
					{
						return true;
					}
					return false;
				}
			}

			public class SyncInfoDetails
			{
				public string Type { get; set; }
				public DateTime Time { get; set; }
				public string file { get; set; }

				private SyncInfoDetails(string sType, string sTime, string sFile)
				{
					Type = sType;
					file = sFile;
					DateTime dtTime;
					if (DateTime.TryParseExact(sTime, "yyyy-MM-dd HH:mm:ss#fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out dtTime))
					{
						Time = dtTime;
					}
				}

				private static Dictionary<string, SyncInfoDetails> GetDetailsFromFile(XDocument xDoc)
				{
					Dictionary<string, SyncInfoDetails> dictDetails = new Dictionary<string, SyncInfoDetails>();
					var ns = xDoc.Root.GetDefaultNamespace();

					var Root = xDoc.Element(ns + "Changes");
					var Changes = Root.Elements(ns + "Change");

					foreach (var change in Changes)
					{
						if (change.Attribute("type") != null && change.Attribute("time") != null && change.Attribute("file") != null)
						{
							string sFile = change.Attribute("file").Value;
							dictDetails[sFile] = new SyncInfoDetails(change.Attribute("type").Value, change.Attribute("time").Value, sFile);
						}
					}

					return dictDetails;
				}

				public static Dictionary<string, SyncInfoDetails> GetSyncDetails(ScriptData scriptdata, string sPath)
				{
					try
					{
						XDocument xDoc = XDocument.Load(sPath);
						return GetDetailsFromFile(xDoc);
					}
					catch (Exception e)
					{
						scriptdata.AddError("Failed to parse SyncInfo for " + sPath + " -> " + e.GetType().Name + ": " + e.Message);
						return null;
					}
				}
			}
			#endregion
		}
	}

	internal class MultiSetComparer<T> : IEqualityComparer<IEnumerable<T>>
	{
		private readonly IEqualityComparer<T> m_comparer;
		public MultiSetComparer(IEqualityComparer<T> comparer = null)
		{
			m_comparer = comparer ?? EqualityComparer<T>.Default;
		}

		public bool Equals(IEnumerable<T> first, IEnumerable<T> second)
		{
			if (first == null)
				return second == null;

			if (second == null)
				return false;

			if (ReferenceEquals(first, second))
				return true;

			if (first is ICollection<T> && second is ICollection<T>)
			{
				ICollection<T> firstCollection = first as ICollection<T>;
				ICollection<T> secondCollection = second as ICollection<T>;
				if (firstCollection.Count != secondCollection.Count)
					return false;

				if (firstCollection.Count == 0)
					return true;
			}

			return !HaveMismatchedElement(first, second);
		}

		private bool HaveMismatchedElement(IEnumerable<T> first, IEnumerable<T> second)
		{
			int firstNullCount;
			int secondNullCount;

			var firstElementCounts = GetElementCounts(first, out firstNullCount);
			var secondElementCounts = GetElementCounts(second, out secondNullCount);

			if (firstNullCount != secondNullCount || firstElementCounts.Count != secondElementCounts.Count)
				return true;

			foreach (var kvp in firstElementCounts)
			{
				var firstElementCount = kvp.Value;
				int secondElementCount;
				secondElementCounts.TryGetValue(kvp.Key, out secondElementCount);

				if (firstElementCount != secondElementCount)
					return true;
			}

			return false;
		}

		private Dictionary<T, int> GetElementCounts(IEnumerable<T> enumerable, out int nullCount)
		{
			var dictionary = new Dictionary<T, int>(m_comparer);
			nullCount = 0;

			foreach (T element in enumerable)
			{
				if (element == null)
				{
					nullCount++;
				}
				else
				{
					int num;
					dictionary.TryGetValue(element, out num);
					num++;
					dictionary[element] = num;
				}
			}

			return dictionary;
		}

		public int GetHashCode(IEnumerable<T> enumerable)
		{
			if (enumerable == null) throw new ArgumentNullException("enumerable");

			int hash = 17;

			foreach (T val in enumerable.OrderBy(x => x))
				hash = hash * 23 + ((val == null) ? 42 : GetHashCode());

			return hash;
		}
	}

	/// <summary>
	/// Provides access to a network share.
	/// </summary>
	internal class NetworkShareAccesser : IDisposable
	{
		private string _remoteUncName;
		private string _remoteComputerName;

		public string RemoteComputerName
		{
			get
			{
				return this._remoteComputerName;
			}
			set
			{
				this._remoteComputerName = value;
				this._remoteUncName = @"\\" + this._remoteComputerName;
			}
		}

		public string UserName
		{
			get;
			set;
		}
		public string Password
		{
			get;
			set;
		}

		#region Consts

		private const int RESOURCE_CONNECTED = 0x00000001;
		private const int RESOURCE_GLOBALNET = 0x00000002;
		private const int RESOURCE_REMEMBERED = 0x00000003;

		private const int RESOURCETYPE_ANY = 0x00000000;
		private const int RESOURCETYPE_DISK = 0x00000001;
		private const int RESOURCETYPE_PRINT = 0x00000002;

		private const int RESOURCEDISPLAYTYPE_GENERIC = 0x00000000;
		private const int RESOURCEDISPLAYTYPE_DOMAIN = 0x00000001;
		private const int RESOURCEDISPLAYTYPE_SERVER = 0x00000002;
		private const int RESOURCEDISPLAYTYPE_SHARE = 0x00000003;
		private const int RESOURCEDISPLAYTYPE_FILE = 0x00000004;
		private const int RESOURCEDISPLAYTYPE_GROUP = 0x00000005;

		private const int RESOURCEUSAGE_CONNECTABLE = 0x00000001;
		private const int RESOURCEUSAGE_CONTAINER = 0x00000002;


		private const int CONNECT_INTERACTIVE = 0x00000008;
		private const int CONNECT_PROMPT = 0x00000010;
		private const int CONNECT_REDIRECT = 0x00000080;
		private const int CONNECT_UPDATE_PROFILE = 0x00000001;
		private const int CONNECT_COMMANDLINE = 0x00000800;
		private const int CONNECT_CMD_SAVECRED = 0x00001000;

		private const int CONNECT_LOCALDRIVE = 0x00000100;

		#endregion

		#region Errors

		private const int NO_ERROR = 0;

		private const int ERROR_ACCESS_DENIED = 5;
		private const int ERROR_ALREADY_ASSIGNED = 85;
		private const int ERROR_BAD_DEVICE = 1200;
		private const int ERROR_BAD_NET_NAME = 67;
		private const int ERROR_BAD_PROVIDER = 1204;
		private const int ERROR_CANCELLED = 1223;
		private const int ERROR_EXTENDED_ERROR = 1208;
		private const int ERROR_INVALID_ADDRESS = 487;
		private const int ERROR_INVALID_PARAMETER = 87;
		private const int ERROR_INVALID_PASSWORD = 1216;
		private const int ERROR_MORE_DATA = 234;
		private const int ERROR_NO_MORE_ITEMS = 259;
		private const int ERROR_NO_NET_OR_BAD_PATH = 1203;
		private const int ERROR_NO_NETWORK = 1222;

		private const int ERROR_BAD_PROFILE = 1206;
		private const int ERROR_CANNOT_OPEN_PROFILE = 1205;
		private const int ERROR_DEVICE_IN_USE = 2404;
		private const int ERROR_NOT_CONNECTED = 2250;
		private const int ERROR_OPEN_FILES = 2401;

		#endregion

		#region PInvoke Signatures

		[DllImport("Mpr.dll")]
		private static extern int WNetUseConnection(
			IntPtr hwndOwner,
			NETRESOURCE lpNetResource,
			string lpPassword,
			string lpUserID,
			int dwFlags,
			string lpAccessName,
			string lpBufferSize,
			string lpResult
			);

		[DllImport("Mpr.dll")]
		private static extern int WNetCancelConnection2(
			string lpName,
			int dwFlags,
			bool fForce
			);

		[StructLayout(LayoutKind.Sequential)]
		private class NETRESOURCE
		{
			public int dwScope = 0;
			public int dwType = 0;
			public int dwDisplayType = 0;
			public int dwUsage = 0;
			public string lpLocalName = "";
			public string lpRemoteName = "";
			public string lpComment = "";
			public string lpProvider = "";
		}

		#endregion

		/// <summary>
		/// Creates a NetworkShareAccesser for the given computer name. The user will be promted to enter credentials
		/// </summary>
		/// <param name="remoteComputerName"></param>
		/// <returns></returns>
		public static NetworkShareAccesser Access(string remoteComputerName)
		{
			return new NetworkShareAccesser(remoteComputerName);
		}

		/// <summary>
		/// Creates a NetworkShareAccesser for the given computer name using the given domain/computer name, username and password.
		/// </summary>
		/// <param name="remoteComputerName"></param>
		/// <param name="domainOrComuterName"></param>
		/// <param name="userName"></param>
		/// <param name="password"></param>
		public static NetworkShareAccesser Access(string remoteComputerName, string domainOrComuterName, string userName, string password)
		{
			return new NetworkShareAccesser(remoteComputerName,
											domainOrComuterName + @"\" + userName,
											password);
		}

		/// <summary>
		/// Creates a NetworkShareAccesser for the given computer name using the given username (format: domainOrComputername\Username) and password
		/// </summary>
		/// <param name="remoteComputerName"></param>
		/// <param name="userName"></param>
		/// <param name="password"></param>
		public static NetworkShareAccesser Access(string remoteComputerName, string userName, string password)
		{
			return new NetworkShareAccesser(remoteComputerName,
											userName,
											password);
		}

		private NetworkShareAccesser(string remoteComputerName)
		{
			RemoteComputerName = remoteComputerName;

			this.ConnectToShare(this._remoteUncName, null, null, true);
		}

		private NetworkShareAccesser(string remoteComputerName, string userName, string password)
		{
			RemoteComputerName = remoteComputerName;
			UserName = userName;
			Password = password;

			this.ConnectToShare(this._remoteUncName, this.UserName, this.Password, false);
		}

		private void ConnectToShare(string remoteUnc, string username, string password, bool promptUser)
		{
			NETRESOURCE nr = new NETRESOURCE
			{
				dwType = RESOURCETYPE_DISK,
				lpRemoteName = remoteUnc
			};

			int result;
			if (promptUser)
			{
				result = WNetUseConnection(IntPtr.Zero, nr, "", "", CONNECT_INTERACTIVE | CONNECT_PROMPT, null, null, null);
			}
			else
			{
				result = WNetUseConnection(IntPtr.Zero, nr, password, username, 0, null, null, null);
			}

			if (result != NO_ERROR)
			{
				throw new Win32Exception(result);
			}
		}

		private void DisconnectFromShare(string remoteUnc)
		{
			int result = WNetCancelConnection2(remoteUnc, CONNECT_UPDATE_PROFILE, false);
			if (result != NO_ERROR)
			{
				throw new Win32Exception(result);
			}
		}

		/// <summary>
		/// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
		/// </summary>
		/// <filterpriority>2</filterpriority>
		public void Dispose()
		{
			this.DisconnectFromShare(this._remoteUncName);
		}
	}
}