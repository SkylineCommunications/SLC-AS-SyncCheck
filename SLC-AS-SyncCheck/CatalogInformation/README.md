# SLC-AS-SyncCheck

## About

This automation script verifies the synchronization of element and service files across a DataMiner System (including Failover Agents).

It is intended to troubleshoot the following errors in large clusters:

- Errors related to duplicate service or element IDs
- Errors occurring when new services reuse the name of old services
- Synchronization errors related to services or elements

For more information, see [Sync Check](https://aka.dataminer.services/Sync_Check).

## Username

The username parameter can be specified in the following ways:

- `alice` (local account)
- `.\alice` (explicit local account)
- `MYDOMAIN\alice` (Active Directory, NETBIOS down-level)
- `alice@corp.example.com` (Active Directory, UPN)

## Prerequisites

- A Windows account with local Administrator rights on the remote DMAs. The script accesses the `C:\Skyline DataMiner` folder through the administrative UNC share `\\&lt;DMA-IP&gt;\c$`.
