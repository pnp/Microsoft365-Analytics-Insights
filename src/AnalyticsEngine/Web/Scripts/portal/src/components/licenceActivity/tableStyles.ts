import { makeStyles, tokens } from '@fluentui/react-components';

/**
 * Shared table styling for the Licence activity tables (SKU assignments, drill-down users), so the
 * two look and behave identically. Mirrors the compact, dependency-free table style used elsewhere
 * in the portal rather than pulling in the heavier DataGrid.
 */
export const useLaTableStyles = makeStyles({
  wrap: {
    overflowX: 'auto',
  },
  table: {
    width: '100%',
    borderCollapse: 'collapse',
  },
  th: {
    textAlign: 'left',
    padding: '6px 10px',
    borderBottomWidth: '1px',
    borderBottomStyle: 'solid',
    borderBottomColor: tokens.colorNeutralStroke2,
    color: tokens.colorNeutralForeground3,
    fontWeight: tokens.fontWeightSemibold,
    whiteSpace: 'nowrap',
  },
  thNumeric: {
    textAlign: 'right',
  },
  td: {
    padding: '6px 10px',
    borderBottomWidth: '1px',
    borderBottomStyle: 'solid',
    borderBottomColor: tokens.colorNeutralStroke3,
    verticalAlign: 'middle',
  },
  tdNumeric: {
    textAlign: 'right',
    fontVariantNumeric: 'tabular-nums',
  },
  selectableRow: {
    cursor: 'pointer',
    ':hover': {
      backgroundColor: tokens.colorNeutralBackground2Hover,
    },
  },
  selectedRow: {
    backgroundColor: tokens.colorBrandBackground2,
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  miniTrack: {
    position: 'relative',
    height: '8px',
    minWidth: '60px',
    borderRadius: tokens.borderRadiusSmall,
    backgroundColor: tokens.colorNeutralBackground3,
    overflow: 'hidden',
  },
  miniBar: {
    height: '100%',
    borderRadius: tokens.borderRadiusSmall,
    backgroundColor: tokens.colorBrandBackground,
  },
});
