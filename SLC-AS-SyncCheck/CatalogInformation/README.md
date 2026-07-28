# SLC-AS-SyncCheck

Verifies the synchronization of element and service files across a DataMiner System (including Failover Agents).

Intended for troubleshooting in large clusters when there are:
  - errors related to duplicate service or element IDs;
  - errors when new services reuse the name of old services;
  - synchronization errors related to services or elements.

Username parameter accepts:
  - alice                     (local account)
  - .\alice                   (explicit local account)
  - MYDOMAIN\alice            (Active Directory, NETBIOS down-level)
  - alice@corp.example.com    (Active Directory, UPN)

Requires a Windows account with local Administrator rights on the remote DMAs — the script accesses the "C:\Skyline DataMiner" folder through the administrative UNC share \\&lt;DMA-IP&gt;\c$.

See: https://docs.dataminer.services/dataminer/DataMiner_Tools/Sync_Check.html