# SLC-AS-SyncCheck

## About

This automation script verifies the synchronization of element and service files across a DataMiner System (including Failover Agents).

It is intended to troubleshoot the following errors in large clusters:

- Errors related to duplicate service or element IDs
- Errors occurring when new services reuse the name of old services
- Synchronization errors related to services or elements

The script produces a per-run report grouped by DMA under `C:\Skyline_Data\SyncCheckResults\`.

For more information, see [Sync Check](https://aka.dataminer.services/Sync_Check).

## Key Features

- **Detect Duplicate IDs**: Identify duplicate service or element IDs across a DataMiner System.
- **Catch Service Reuse Errors**: Flag issues when a new service reuses the name of an old, removed service.
- **Verify Synchronization**: Check elements and services for synchronization errors, including across Failover Agents.
- **Generate Per-DMA Reports**: Produce a structured report grouped by DMA for easy review.
- **Support Large Clusters**: Built to troubleshoot synchronization issues in large, multi-DMA clusters.

## Username

The username parameter can be specified in the following ways:

- `alice` (local account)
- `.\alice` (explicit local account)
- `MYDOMAIN\alice` (Active Directory, NETBIOS down-level)
- `alice@corp.example.com` (Active Directory, UPN)

## Prerequisites

- DataMiner 10.4.0.0 (build 14003) or later.
- A Windows account with local Administrator rights on the remote DMAs. The script accesses the `C:\Skyline DataMiner` folder through the administrative UNC share `\\<DMA-IP>\c$`.
