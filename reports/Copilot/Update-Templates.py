"""Embed the adjacent, testable SQL in the legacy templates; preserve all other ZIP entries."""
import json
from pathlib import Path
import zipfile


def expression(sql_file, server, database):
    sql = Path(__file__).with_name(sql_file).read_text(encoding="utf-8").strip()
    sql = sql.replace('"', '""').replace("\n", "#(lf)")
    return ["let", f'    Source = Sql.Database({server}, {database}, [Query="{sql}"])', "in", "    Source"]


root = Path(__file__).parent
for name in ["Copilot Activity Dashboard.pbit", "Copilot Agent Interactions.pbit"]:
    path = root / name
    with zipfile.ZipFile(path) as archive:
        entries = [(entry, archive.read(entry.filename)) for entry in archive.infolist()]
    output = []
    for entry, data in entries:
        if entry.filename == "DataModelSchema":
            model = json.loads(data.decode("utf-16le"))
            for table in model["model"]["tables"]:
                query = {"Copilot Agents": "agents.sql", "Copilot Chats": "chats.sql",
                         "Copilot Accessed Resource Lookups": "resources.sql",
                         "CopilotActivity": "activity.sql"}.get(table["name"])
                if query:
                    server, database = ('ServerName', 'DatabaseName') if table["name"] == "CopilotActivity" else ('"localhost"', '"Analytics"')
                    table["partitions"][0]["source"]["expression"] = expression(query, server, database)
                if table["name"] == "Copilot Agents":
                    for column in table["columns"]:
                        if column["name"] == "Is Custom Agent":
                            column.update(name="Agent origin", sourceColumn="Agent origin", dataType="string")
                            column.pop("formatString", None)
                if table["name"] == "Copilot Chats":
                    for column in table["columns"]:
                        if column["name"] == "Credit Estimate":
                            column.update(name="Audit credit estimate", sourceColumn="Audit credit estimate",
                                          description="Audit-derived estimate, not billed consumption. Missing historical evidence is not zero.")
                    if not any(c["name"] == "Estimate status" for c in table["columns"]):
                        table["columns"].append({"name": "Estimate status", "sourceColumn": "Estimate status",
                                                 "dataType": "string", "summarizeBy": "none"})
                for measure in table.get("measures", []):
                    value = measure.get("expression")
                    if value is not None:
                        text = json.dumps(value).replace("Custom agent", "Customer-built").replace("Standard Copilot", "Microsoft")
                        measure["expression"] = json.loads(text)
            data = json.dumps(model, ensure_ascii=False, indent=2).encode("utf-16le")
        if entry.filename.endswith(".json") or entry.filename == "DataModelSchema":
            encoding = "utf-16le" if entry.filename == "DataModelSchema" else "utf-8"
            text = data.decode(encoding)
            text = text.replace("Is Custom Agent", "Agent origin").replace("Credit Estimate", "Audit credit estimate")
            text = text.replace("Custom agent", "Customer-built").replace("Standard Copilot", "Microsoft")
            data = text.encode(encoding)
            if entry.filename == "Report/definition/report.json":
                report = json.loads(text)
                for report_filter in report.get("filterConfig", {}).get("filters", []):
                    if report_filter.get("field", {}).get("Column", {}).get("Property") == "Agent origin":
                        # Do not retain the old boolean "true" filter on a string column, or hide Unknown.
                        report_filter.pop("filter", None)
                data = json.dumps(report, ensure_ascii=False, separators=(",", ":")).encode(encoding)
        output.append((entry, data))
    with zipfile.ZipFile(path, "w") as archive:
        for entry, data in output:
            archive.writestr(entry, data)
