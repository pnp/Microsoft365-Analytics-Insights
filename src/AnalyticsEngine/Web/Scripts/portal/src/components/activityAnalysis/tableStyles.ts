import { makeStyles, tokens } from '@fluentui/react-components';

/** Height of the matrix's first header row: room for a metric name on two lines. */
export const HEAD_ROW_HEIGHT = 52;

/**
 * Table styling shared by the results matrix and the top people, so the two read as one design: a
 * scrolling region with a sticky header, a sticky first column for the row's name, and right-aligned
 * tabular figures.
 *
 * The cells are `border-collapse: separate` because sticky cells in a collapsed table lose their
 * borders as they scroll - the line under the header would vanish exactly when it is needed.
 */
export const useActivityTableStyles = makeStyles({
  scroll: {
    overflow: 'auto',
    maxHeight: 'min(70vh, 680px)',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
    ':focus-visible': {
      outline: `2px solid ${tokens.colorStrokeFocus2}`,
      outlineOffset: '1px',
    },
    // On paper there is no scrolling: the whole table is printed, and its header row repeats.
    '@media print': {
      maxHeight: 'none',
      overflow: 'visible',
    },
  },
  table: {
    borderCollapse: 'separate',
    borderSpacing: 0,
    minWidth: '100%',
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
  },
  caption: {
    position: 'absolute',
    width: '1px',
    height: '1px',
    overflow: 'hidden',
    clipPath: 'inset(50%)',
    whiteSpace: 'nowrap',
  },
  th: {
    position: 'sticky',
    top: 0,
    zIndex: 2,
    boxSizing: 'border-box',
    padding: '6px 10px',
    textAlign: 'right',
    verticalAlign: 'bottom',
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground2,
    backgroundColor: tokens.colorNeutralBackground3,
    borderBottomWidth: '1px',
    borderBottomStyle: 'solid',
    borderBottomColor: tokens.colorNeutralStroke2,
    whiteSpace: 'nowrap',
  },
  thFirstRow: {
    height: `${HEAD_ROW_HEIGHT}px`,
  },
  thSecondRow: {
    top: `${HEAD_ROW_HEIGHT}px`,
    fontWeight: tokens.fontWeightRegular,
    color: tokens.colorNeutralForeground3,
  },
  thLeft: {
    textAlign: 'left',
  },
  thMetric: {
    textAlign: 'center',
    verticalAlign: 'middle',
    whiteSpace: 'normal',
    minWidth: '150px',
    maxWidth: '220px',
  },
  metricName: {
    display: '-webkit-box',
    WebkitLineClamp: 2,
    WebkitBoxOrient: 'vertical',
    overflow: 'hidden',
  },
  // The left edge of a metric's pair of columns, so each metric reads as one block.
  groupStart: {
    borderLeftWidth: '1px',
    borderLeftStyle: 'solid',
    borderLeftColor: tokens.colorNeutralStroke2,
  },
  sortButton: {
    minWidth: 0,
    height: 'auto',
    padding: '0 2px',
    fontWeight: 'inherit',
    fontSize: 'inherit',
    lineHeight: 'inherit',
    color: 'inherit',
  },
  td: {
    boxSizing: 'border-box',
    padding: '6px 10px',
    textAlign: 'right',
    fontVariantNumeric: 'tabular-nums',
    whiteSpace: 'nowrap',
    borderBottomWidth: '1px',
    borderBottomStyle: 'solid',
    borderBottomColor: tokens.colorNeutralStroke3,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  // The row's name: pinned to the left edge while the figures scroll under it.
  first: {
    position: 'sticky',
    left: 0,
    zIndex: 1,
    textAlign: 'left',
    fontWeight: tokens.fontWeightRegular,
    minWidth: '220px',
    maxWidth: '320px',
    borderRightWidth: '1px',
    borderRightStyle: 'solid',
    borderRightColor: tokens.colorNeutralStroke2,
  },
  // The header cell above the pinned column sits over both the header and the column.
  corner: {
    left: 0,
    zIndex: 3,
    textAlign: 'left',
    minWidth: '220px',
    borderRightWidth: '1px',
    borderRightStyle: 'solid',
    borderRightColor: tokens.colorNeutralStroke2,
  },
  nameCell: {
    display: 'flex',
    alignItems: 'center',
    gap: '4px',
    minWidth: 0,
  },
  name: {
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  nameDetail: {
    color: tokens.colorNeutralForeground3,
    whiteSpace: 'nowrap',
  },
  expanderSpacer: {
    display: 'inline-block',
    width: '24px',
    flexShrink: 0,
  },
  expander: {
    minWidth: '24px',
    width: '24px',
    height: '24px',
    padding: 0,
    flexShrink: 0,
  },
  row: {
    '&:hover>td': {
      backgroundColor: tokens.colorNeutralBackground1Hover,
    },
    '&:hover>th': {
      backgroundColor: tokens.colorNeutralBackground1Hover,
    },
  },
  otherRow: {
    '&>td': {
      color: tokens.colorNeutralForeground3,
      fontStyle: 'italic',
    },
    '&>th': {
      color: tokens.colorNeutralForeground3,
      fontStyle: 'italic',
    },
  },
  personRow: {
    '&>td': {
      backgroundColor: tokens.colorNeutralBackground2,
    },
    '&>th': {
      backgroundColor: tokens.colorNeutralBackground2,
    },
  },
  personName: {
    paddingLeft: '48px',
    color: tokens.colorNeutralForeground2,
  },
  noteRow: {
    '&>td': {
      textAlign: 'left',
      whiteSpace: 'normal',
      color: tokens.colorNeutralForeground3,
      backgroundColor: tokens.colorNeutralBackground2,
    },
  },
  noteContent: {
    position: 'sticky',
    left: '38px',
    display: 'inline-flex',
    alignItems: 'center',
    gap: '8px',
    flexWrap: 'wrap',
    paddingLeft: '28px',
  },
  // The Total row stays in view at the foot of the scrolling region, as in the Power BI matrix.
  total: {
    '&>td': {
      position: 'sticky',
      bottom: 0,
      zIndex: 2,
      fontWeight: tokens.fontWeightSemibold,
      backgroundColor: tokens.colorNeutralBackground3,
      borderTopWidth: '2px',
      borderTopStyle: 'solid',
      borderTopColor: tokens.colorNeutralStroke1,
      borderBottomWidth: 0,
    },
    '&>th': {
      position: 'sticky',
      bottom: 0,
      zIndex: 3,
      fontWeight: tokens.fontWeightSemibold,
      backgroundColor: tokens.colorNeutralBackground3,
      borderTopWidth: '2px',
      borderTopStyle: 'solid',
      borderTopColor: tokens.colorNeutralStroke1,
      borderBottomWidth: 0,
    },
  },
  highlight: {
    color: tokens.colorBrandForeground1,
    fontWeight: tokens.fontWeightSemibold,
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
});
