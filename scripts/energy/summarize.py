# What processes cost between two snapshots: CPU time and idle wake-ups, the
# two things a VPN spends the battery on (the screen and radio cost the same
# in every app). On an iPhone Instruments' Activity Monitor samples each
# process once per recording, with running totals since the process started,
# so a snapshot is a short recording and a cost is one snapshot minus another.
#
# usage: summarize.py snapshot <activity-monitor-process-live.xml> <unix-time>
#            prints a snapshot as TSV
#        summarize.py diff <before.tsv> <after.tsv> <process-name>...
#            the named processes, their sum, and the busiest processes overall
import sys
import xml.etree.ElementTree as ET


def snapshot(path, when):
    node = ET.parse(path).getroot().find(".//node")
    cols = [c.findtext("mnemonic") for c in node.find("schema").findall("col")]
    seen = {}  # xctrace writes each value once with id= and then ref= to it

    def value(el):
        if "ref" in el.attrib:
            return seen[el.attrib["ref"]]
        for child in el.iter():
            if "id" in child.attrib:
                seen[child.attrib["id"]] = child
        return el

    def num(el):
        return int(el.text) if el is not None and el.text and el.text.strip().isdigit() else 0

    print(f"# {when}")
    for row in node.findall("row"):
        cells = dict(zip(cols, (value(c) for c in row)))
        proc = cells.get("process")
        if proc is None or proc.tag != "process":
            continue
        name = proc.attrib.get("fmt", "?").rsplit(" (", 1)[0]
        print(f"{num(cells.get('pid'))}\t{name}\t{num(cells.get('cpu-total'))}\t{num(cells.get('idle-wakeups'))}")


def load(path):
    procs, when = {}, 0.0
    for line in open(path, encoding="utf-8"):
        if line.startswith("# "):
            when = float(line[2:])
            continue
        pid, name, cpu, wake = line.rstrip("\n").split("\t")
        procs[int(pid)] = (name, int(cpu), int(wake))
    return procs, when


def diff(before_path, after_path, names):
    before, t0 = load(before_path)
    after, t1 = load(after_path)
    minutes = max((t1 - t0) / 60, 1e-9)
    cost = []
    for pid, (name, cpu, wake) in after.items():
        # A process started in between costs everything it has used.
        _, cpu0, wake0 = before.get(pid, (name, 0, 0))
        cost.append((name, pid, (cpu - cpu0) / 1e6, wake - wake0, pid not in before))

    print(f"между снимками: {minutes:.1f} мин")
    # Activity Monitor cuts names to 31 characters.
    names = {n.lower()[:31] for n in names}
    picked = [c for c in cost if c[0].lower()[:31] in names]
    for name, pid, ms, wake, new in sorted(picked, key=lambda c: -c[2]):
        print(f"  {name} ({pid}){' — запущен между снимками' if new else ''}: CPU {ms:.0f} мс, пробуждений {wake}")
    gone = [f"{n} ({p})" for p, (n, _, _) in before.items()
            if p not in after and n.lower()[:31] in names]
    if gone:
        print(f"  завершились между снимками, их расход не учтён: {', '.join(gone)}")
    ms = sum(c[2] for c in picked)
    wake = sum(c[3] for c in picked)
    print(f"ИТОГО\t{ms:.0f}\t{wake}\t{ms / minutes:.1f}\t{wake / minutes:.1f}\t{minutes:.1f}")
    print("больше всего CPU за это время:")
    for name, pid, ms, wake, _ in sorted(cost, key=lambda c: -c[2])[:12]:
        print(f"  {ms:9.0f} мс  {wake:7d} проб.  {name} ({pid})")


if sys.argv[1] == "snapshot":
    snapshot(sys.argv[2], sys.argv[3])
else:
    diff(sys.argv[2], sys.argv[3], sys.argv[4:])
