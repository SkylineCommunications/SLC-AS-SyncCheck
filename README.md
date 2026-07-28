# SLC-AS-SyncCheck

## About

SLC-AS-SyncCheck is a DataMiner Automation script that audits synchronization of Element, Service and RemoteService files across a DMA cluster. For every DataMiner Agent (DMA) in the cluster it:

- Discovers all agents and their Failover pairing (Main/Backup) via SLNet.
- Reads each agent's on-disk Element, Service and RemoteService folders over a network share (using the supplied Windows credentials).
- Compares the Main agent's folder contents against its Backup partner's folder contents to detect drift between Failover peers.
- Compares on-disk elements/services against what SLNet currently reports, to detect mismatches between disk and the running system.
- Cross-checks RemoteService references against the elements/services actually found on the other DMAs in the cluster.
- Optionally compares each element's/service's `SyncInfo.xml` between Main and Backup.

The script produces a single, per-run text report grouped by DMA, so operators can quickly see which agents/pairs have out-of-sync files without manually browsing UNC paths.

## Getting started

### Prerequisites

- A DataMiner System with an Automation module.
- A Windows account (local or domain) with read access to the DataMiner Elements, Services and RemoteServices folders (typically under `%DataMiner installation folder%\SLElement`, `SLService`, `SLRemoteService`) on every DMA in the cluster.

### Script parameters

| Parameter | Description |
|---|---|
| `Username` | Windows account used to connect to each agent's file share. Supports `alice` (local), `.\alice` (local), `DOMAIN\alice` (NETBIOS) and `alice@corp.example.com` (UPN) formats. |
| `Password` | Password for the account above. |

### Running the script

1. Deploy the `.dmapp` package (built from this project) to your DataMiner System, or install it via the Catalog.
2. Run the `SLC-AS-SyncCheck` Automation script, providing the `Username` and `Password` parameters.
3. Once the script completes, the report is written to:

   ```
   C:\Skyline_Data\SyncCheckResults\SyncCheckResult_<yyyyMMdd_HHmm>.txt
   ```

   on the DMA where the script ran. The report contains:
   - A `DEBUG LOGGING` section with the resolved connection identity, discovered agents, Failover pairs and step-by-step progress.
   - An `ERRORS` section grouped by source DMA (`Main`/`Backup` → IP/ComputerName), listing every synchronization or parsing issue found.

## About DataMiner

DataMiner is a transformational platform that provides vendor-independent control and monitoring of devices and services. Out of the box, it addresses key challenges such as security, complexity, multi-vendor, and much more. It has a pronounced open architecture and powerful capabilities enabling users to evolve easily and continuously.

The foundation of DataMiner is its powerful and versatile data acquisition and control layer. With DataMiner Agents (DMAs) by your side, you can unify all your systems, devices, and services, per country, globally, or on a hybrid-cloud platform.

The middleware and independence of DataMiner are vital elements to provide the most local and industry-adaptable solutions. At the same time, it provides a valuable and long-term solution for future generations.

## About Skyline Communications

At Skyline Communications, we deal in world-class solutions that are deployed by leading companies around the globe. Check out [our proven track record](https://www.skyline.be/en/customers) and see how we make our customers' lives easier by empowering them to take their operations to the next level.
