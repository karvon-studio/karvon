#!/usr/bin/env python3
"""A fake kubectl for the Kubernetes tests: a cluster's objects from FAKE_KUBECTL_DATA/state.json, every call logged in calls.txt."""
import json
import os
import sys
import time

DATA = os.environ["FAKE_KUBECTL_DATA"]
args = sys.argv[1:]
with open(os.path.join(DATA, "calls.txt"), "a") as log:
    log.write(" ".join(args) + "\n")
with open(os.path.join(DATA, "state.json")) as f:
    state = json.load(f)

RESOURCES = {
    "deployments.apps": "Deployment", "statefulsets.apps": "StatefulSet", "daemonsets.apps": "DaemonSet", "jobs.batch": "Job",
    "cronjobs.batch": "CronJob", "pods": "Pod", "pod": "Pod", "services": "Service", "ingresses.networking.k8s.io": "Ingress",
    "configmaps": "ConfigMap", "secrets": "Secret", "applications.argoproj.io": "Application",
    "kustomizations.kustomize.toolkit.fluxcd.io": "Kustomization", "helmreleases.helm.toolkit.fluxcd.io": "HelmRelease",
}


def fail(message, code=1):
    sys.stderr.write(message + "\n")
    sys.exit(code)


context, namespace, rest, i = None, None, [], 0
while i < len(args):
    if args[i] == "--context":
        context = args[i + 1]
        i += 2
    elif args[i] == "--namespace":
        namespace = args[i + 1]
        i += 2
    else:
        rest.append(args[i])
        i += 1
flags = [a for a in rest if a.startswith("-")]
words = [a for a in rest if not a.startswith("-")]
output_json = "json" in rest and "-o" in rest
if "-o" in rest:
    words.remove(rest[rest.index("-o") + 1])

if words[:2] == ["config", "view"]:
    print(json.dumps(state["config"]))
    sys.exit(0)

if context is not None and context not in [c["name"] for c in state["config"]["contexts"]]:
    fail(f'error: context "{context}" does not exist')
if state.get("unreachable"):
    fail("Unable to connect to the server: dial tcp 10.0.0.1:6443: i/o timeout")
namespace = namespace or "default"
everywhere = "--all-namespaces" in flags


def objects(kinds):
    return [o for o in state["objects"] if o["kind"] in kinds and (everywhere or o["metadata"].get("namespace") == namespace)]


def matches(o, selector):
    labels = o["metadata"].get("labels", {})
    return all(labels.get(k) == v for k, v in (p.split("=") for p in selector.split(",")))


verb = words[0] if words else ""
if verb == "api-resources":
    print("\n".join(state.get("api_resources", [])))
elif verb == "get" and words[1:2] == ["namespaces"]:
    print(json.dumps({"kind": "List", "items": [{"metadata": {"name": n}} for n in state["namespaces"]]}))
elif verb == "get" and words[1:2] == ["events"]:
    selector = next((a.split("=", 1)[1] for a in flags if a.startswith("--field-selector=")), "")
    name = next((p.split("=")[1] for p in selector.split(",") if p.startswith("involvedObject.name=")), None)
    items = [e for e in state.get("events", []) if e["metadata"]["namespace"] == namespace and (name is None or e["involvedObject"]["name"] == name)]
    print(json.dumps({"kind": "List", "items": items}))
elif verb == "get" and "-l" in rest:
    selector = rest[rest.index("-l") + 1]
    print(json.dumps({"kind": "List", "items": [o for o in objects({"Pod"}) if matches(o, selector)]}))
elif verb == "get" and len(words) == 2 and "/" in words[1] and "yaml" in rest:
    print(f"kind: {RESOURCES[words[1].split('/')[0]]}\nmetadata:\n  name: {words[1].split('/')[1]}\n  namespace: {namespace}")
elif verb == "get" and len(words) == 3:
    kind = RESOURCES[words[1]]
    found = [o for o in objects({kind}) if o["metadata"]["name"] == words[2]]
    if not found:
        fail(f'Error from server (NotFound): {words[1]} "{words[2]}" not found')
    print(json.dumps(found[0]))
elif verb == "get":
    kinds = {RESOURCES[r] for r in words[1].split(",")}
    print(json.dumps({"apiVersion": "v1", "kind": "List", "items": objects(kinds)}))
elif verb == "describe":
    print(state.get("describe", f"Name: {words[-1]}"))
elif verb == "logs":
    target = words[1]
    pods = [o["metadata"]["name"] for o in objects({"Pod"})] if "/" in target else [target]
    for pod in pods:
        for line in state.get("logs", {}).get(pod, []):
            print(f"[pod/{pod}/web] {line}" if "--prefix" in flags else line, flush=True)
    if "--follow" in flags:
        time.sleep(60)
elif verb == "port-forward":
    local, remote = words[-1].split(":")
    print(f"Forwarding from 127.0.0.1:{local} -> {remote}", flush=True)
    time.sleep(60)
elif verb == "rollout" and words[1] == "restart":
    print(f"{words[2]} restarted")
elif verb == "rollout" and words[1] == "undo":
    print(f"{words[2]} rolled back")
elif verb == "scale":
    print(f"{words[1]} scaled")
elif verb == "delete":
    print(f'{words[1]} "{words[2] if len(words) > 2 else ""}" deleted')
elif verb in ("patch", "annotate", "create"):
    print(f"{words[-1]} {verb}ed")
else:
    fail("fake kubectl: " + " ".join(args), 2)
