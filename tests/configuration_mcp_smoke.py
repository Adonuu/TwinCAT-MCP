"""Exercise the real stdio MCP surface. No downloads or runtime state changes.

Default: tool discovery and every configuration tool's policy-only dry run.
--live: also attach to the first accessible IDE for read-only checks.
--template PATH: also create a separate scratch solution under obj/mcp-smoke.
Scratch files are retained for inspection. Never modifies an attached user's project.
"""
import argparse
import json
from pathlib import Path
import queue
import subprocess
import threading
import uuid


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--live", action="store_true")
    parser.add_argument("--template")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    scratch = root / "obj" / "mcp-smoke" / uuid.uuid4().hex
    scratch.mkdir(parents=True)
    log = (scratch / "server.log").open("w", encoding="utf-8")
    server = subprocess.Popen(
        ["dotnet", str(root / "src/TwinCatMcp.Server/bin/Debug/net10.0/TwinCatMcp.Server.dll")],
        cwd=root, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=log, text=True,
    )
    messages = queue.Queue()
    def receive():
        for line in server.stdout:
            messages.put(line)

    threading.Thread(target=receive, daemon=True).start()
    request_id = 0

    def rpc(method, params):
        nonlocal request_id
        request_id += 1
        server.stdin.write(json.dumps({"jsonrpc": "2.0", "id": request_id, "method": method, "params": params}) + "\n")
        server.stdin.flush()
        while True:
            result = json.loads(messages.get(timeout=90))
            if result.get("id") != request_id:
                continue
            if "error" in result:
                raise AssertionError(result["error"])
            return result["result"]

    def call(tool_name, **arguments):
        result = rpc("tools/call", {"name": tool_name, "arguments": arguments})
        assert not result.get("isError"), (tool_name, result)
        value = json.loads(result["content"][0]["text"])
        if isinstance(value, dict) and "Succeeded" in value:
            assert value["Succeeded"], (tool_name, value)
        print(tool_name, "passed", flush=True)
        return value

    try:
        rpc("initialize", {"protocolVersion": "2025-03-26", "capabilities": {}, "clientInfo": {"name": "configuration-smoke", "version": "1"}})
        server.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
        server.stdin.flush()
        tools = rpc("tools/list", {})["tools"]
        configuration_tools = [tool for tool in tools if "dryRun checks policy only" in tool.get("description", "")]
        assert len(configuration_tools) >= 25
        for tool in configuration_tools:
            # Policy dry runs deliberately do no argument/path validation or COM invocation.
            schema = tool["inputSchema"]
            defaults = {"string": "Smoke", "integer": 1, "number": 1, "boolean": False, "array": [], "object": {}}
            values = {key: defaults.get(schema["properties"][key].get("type"), "Smoke") for key in schema.get("required", [])}
            if tool["name"] == "set_plc_boot_project":
                # Downloads remain disabled; verify the blocked response explicitly.
                result = rpc("tools/call", {"name": tool["name"], "arguments": values})
                blocked = json.loads(result["content"][0]["text"])
                assert not blocked["Applied"] and not blocked["Succeeded"]
                continue
            preview = call(tool["name"], **values, dryRun=True)
            assert preview["DryRun"] and not preview["Applied"]
        print("Configuration mutation schemas/policy paths:", len(configuration_tools), flush=True)

        if args.live:
            instances = call("list_xae_shell_instances")
            assert instances, "No running shell is accessible in this Windows user/elevation context."
            call("attach_xae_shell", instanceId=instances[0]["InstanceId"])
            projects = call("list_twincat_projects")
            assert projects
            call("get_xae_target_net_id")
            call("get_xae_tree_xml", treePath="TIRT")
            call("get_mapping_info")
            call("disconnect_xae_shell")

        if args.template:
            solution = scratch / "ConfigurationSmoke.sln"
            call("create_xae_solution", solutionPath=str(solution))
            call("add_twincat_project", name="ConfigurationSmoke", templatePath=args.template)
            projects = call("list_twincat_projects")
            assert projects[0]["Selected"]
            call("select_twincat_project", name="ConfigurationSmoke")
            call("set_xae_target_net_id", amsNetId="127.0.0.1.1.1")
            call("set_xae_target_platform", platform="TwinCAT RT (x64)")
            master = call("create_ethercat_master", name="Master")["Value"]
            call("create_ethercat_box", parentTreePath=master["TreePath"], name="Coupler", productInfo="EK1100")
            call("get_device_adapter", treePath=master["TreePath"])
            call("set_device_adapter", treePath=master["TreePath"], description="Scratch adapter", deviceGuid=str(uuid.uuid4()), mac="001122334455")
            task = call("create_realtime_task", name="ImageTask")["Value"]
            call("add_task_image_variable", taskTreePath=task["TreePath"], name="Input", dataType="DINT")
            call("configure_xae_task", treePath=task["TreePath"], cycleTimeUs=2000, priority=20, coreId=0)
            settings = call("get_xae_task_settings", treePath=task["TreePath"])
            assert settings["CycleTimeUs"] == 2000 and settings["Priority"] == 20, settings
            call("configure_xae_realtime_cores", totalCores=2, isolatedCores=0, cores=[{"CoreId": 0, "BaseTimeUs": 1000}])
            call("add_plc_project", name="SmokePlc")
            call("add_plc_library", plcName="SmokePlc", libraryName="Tc2_Utilities", company="Beckhoff Automation GmbH")
            call("remove_plc_reference", plcName="SmokePlc", libraryName="Tc2_Utilities")
            call("add_plc_placeholder", plcName="SmokePlc", placeholderName="SmokeStandard", libraryName="Tc2_Standard", company="Beckhoff Automation GmbH")
            call("create_realtime_task", name="Background", withImage=False)
            call("add_plc_task", plcName="SmokePlc", taskName="Background", programName="MAIN")
            call("check_plc_objects", plcName="SmokePlc")
            source = scratch / "SmokeGVL.TcGVL"
            source.write_text('<TcPlcObject Version="1.1.0.1"><GVL Name="SmokeGVL" Id="{' + str(uuid.uuid4()) + '}"><Declaration><![CDATA[VAR_GLOBAL\n nSmoke : INT;\nEND_VAR]]></Declaration></GVL></TcPlcObject>', encoding="utf-8")
            call("import_plc_source_files", parentTreePath="TIPC^SmokePlc^SmokePlc Project", filePaths=[str(source)])
            call("get_mapping_info")
            call("save_project")
            call("disconnect_xae_shell")
            print("Scratch solution:", solution, flush=True)
    finally:
        server.stdin.close()
        try:
            server.wait(timeout=15)
        except subprocess.TimeoutExpired:
            server.kill()
            server.wait()
        log.close()
        print("Server log:", scratch / "server.log", flush=True)


if __name__ == "__main__":
    main()
