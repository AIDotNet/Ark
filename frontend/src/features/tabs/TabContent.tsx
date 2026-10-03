import type { WorkspaceTab } from "@/stores/workspace"
import { DataGrid } from "@/features/grid/DataGrid"
import { TableDesigner } from "@/features/designer/TableDesigner"
import { QueryConsole } from "@/features/query/QueryConsole"
import { SyncCenter } from "@/features/sync/SyncCenter"

export function TabContent({ tab }: { tab: WorkspaceTab }) {
  switch (tab.kind) {
    case "grid":
      return (
        <DataGrid
          connectionId={tab.connectionId}
          database={tab.database}
          schema={tab.schema}
          table={tab.table!}
        />
      )
    case "designer":
      return (
        <TableDesigner
          connectionId={tab.connectionId}
          database={tab.database}
          schema={tab.schema}
          table={tab.table}
        />
      )
    case "query":
      return (
        <QueryConsole
          tabKey={tab.key}
          connectionId={tab.connectionId}
          connectionName={tab.connectionName}
          initialDatabase={tab.database}
          initialSchema={tab.schema}
        />
      )
    case "sync":
      return <SyncCenter />
  }
}
