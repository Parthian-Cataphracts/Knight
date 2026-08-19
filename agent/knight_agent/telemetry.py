"""
What the agent measures about the machine it runs on.

Deliberately dependency-free. An agent is a daemon on somebody else's
infrastructure, and every package it needs is a package that has to be installed,
kept current and trusted there — so the numbers come from the standard library
and from reading `/proc` where Linux offers it, rather than from `psutil`.

Every reading is best-effort. A metric this cannot obtain is reported as zero or
omitted, never as an exception: an agent that stops heart-beating because it
could not read a network counter has turned a cosmetic gap into an outage.
"""

from __future__ import annotations

import logging
import os
import shutil
import time
from dataclasses import asdict, dataclass

logger = logging.getLogger(__name__)


@dataclass
class Sample:
    """One reading, in the shape KNIGHT's heartbeat expects."""

    cpuPercent: float = 0.0
    memoryUsedBytes: int = 0
    memoryTotalBytes: int = 0
    diskUsedBytes: int = 0
    diskTotalBytes: int = 0
    netInBytes: int = 0
    netOutBytes: int = 0
    loadAverage: float | None = None

    def as_payload(self) -> dict:
        return asdict(self)


def collect(path: str = "/") -> Sample:
    """A full sample of the machine, with whatever this platform can tell us."""
    sample = Sample()

    _read_disk(sample, path)
    _read_memory(sample)
    _read_cpu(sample)
    _read_network(sample)
    _read_load(sample)

    return sample


def _read_disk(sample: Sample, path: str) -> None:
    try:
        usage = shutil.disk_usage(path if os.path.exists(path) else os.getcwd())
        sample.diskTotalBytes = usage.total
        sample.diskUsedBytes = usage.used
    except OSError:
        logger.debug("Disk usage could not be read.", exc_info=True)


def _read_memory(sample: Sample) -> None:
    """
    Memory from /proc/meminfo, using MemAvailable rather than MemFree.

    MemFree counts only genuinely untouched pages and so reports a healthy Linux
    box as almost out of memory — the page cache is doing its job. MemAvailable is
    the kernel's own estimate of what a new process could actually get, which is
    the number an operator means by "free".
    """
    try:
        with open("/proc/meminfo", encoding="utf-8") as handle:
            values = {}
            for line in handle:
                key, _, rest = line.partition(":")
                values[key.strip()] = int(rest.strip().split()[0]) * 1024

        total = values.get("MemTotal", 0)
        available = values.get("MemAvailable", values.get("MemFree", 0))

        sample.memoryTotalBytes = total
        sample.memoryUsedBytes = max(0, total - available)
    except (OSError, ValueError, IndexError):
        logger.debug("Memory could not be read; this platform has no /proc/meminfo.", exc_info=True)


def _read_cpu(sample: Sample) -> None:
    """
    CPU as a percentage, sampled over a short interval.

    CPU use is a rate, not a level: /proc/stat gives cumulative counters, so a
    single read says how busy the machine has been since boot, which is never what
    anybody wants. Two reads a moment apart give the figure the dashboard means.
    """
    first = _cpu_times()
    if first is None:
        return

    time.sleep(0.2)
    second = _cpu_times()
    if second is None:
        return

    idle_delta = second[1] - first[1]
    total_delta = second[0] - first[0]

    if total_delta > 0:
        sample.cpuPercent = round(max(0.0, min(100.0, (1 - idle_delta / total_delta) * 100)), 2)


def _cpu_times() -> tuple[int, int] | None:
    try:
        with open("/proc/stat", encoding="utf-8") as handle:
            fields = [int(value) for value in handle.readline().split()[1:]]
    except (OSError, ValueError, IndexError):
        return None

    if len(fields) < 5:
        return None

    # idle + iowait: a machine waiting on disk is not doing work.
    return sum(fields), fields[3] + fields[4]


def _read_network(sample: Sample) -> None:
    """Cumulative bytes across real interfaces, skipping loopback."""
    try:
        with open("/proc/net/dev", encoding="utf-8") as handle:
            lines = handle.readlines()[2:]
    except OSError:
        return

    received = 0
    transmitted = 0

    for line in lines:
        name, _, rest = line.partition(":")
        if name.strip() == "lo":
            continue

        fields = rest.split()
        if len(fields) >= 9:
            try:
                received += int(fields[0])
                transmitted += int(fields[8])
            except ValueError:
                continue

    sample.netInBytes = received
    sample.netOutBytes = transmitted


def _read_load(sample: Sample) -> None:
    try:
        sample.loadAverage = round(os.getloadavg()[0], 2)
    except (OSError, AttributeError):
        # Windows has no load average, and that is a fact about the platform
        # rather than a failure worth reporting.
        sample.loadAverage = None
