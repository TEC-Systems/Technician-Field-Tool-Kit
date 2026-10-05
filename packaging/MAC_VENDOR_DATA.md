# MAC prefix registry

`mac-vendors.tsv` is a snapshot of the [MacVendorCheck CSV export](https://macvendorcheck.com/downloads), downloaded on 2026-10-05. The publisher says its compiled export is free for personal or commercial use. Its source is the IEEE MA-L, MA-M, and MA-S public registries. Refresh this snapshot when publishing future toolkit updates.

The toolkit embeds this file for offline lookup. The Manufacturer column identifies the registered owner of a visible MAC prefix, not necessarily the brand of the finished device. Locally assigned MAC addresses, devices outside the local ARP domain, and devices with no visible MAC cannot be reliably identified this way.
