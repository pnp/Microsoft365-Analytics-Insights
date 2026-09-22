import { Button, Tooltip } from '@fluentui/react-components';
import { Print16Regular } from '@fluentui/react-icons';
import { useT } from '../../i18n';

/**
 * Prints the report currently on screen, without the app shell around it.
 *
 * There is no print-specific rendering behind this: the whole job is done by the `@media print`
 * block in index.css, which hides everything marked `data-print="hide"` (the brand bar, the area
 * switcher, the nav rail, the page's own filter controls and this button) and flattens the
 * `data-print="content"` wrappers so the report gets the full width of the sheet. Printing with
 * Ctrl+P therefore produces exactly the same output as this button - the button exists because
 * nobody expects a web dashboard to print well, so nobody tries.
 *
 * Only the tab on screen is printed, which is what "print this" means to the person clicking it;
 * the Excel export is the way to get everything at once.
 */
export default function PrintButton({
  tooltip,
  label,
}: {
  /** What this printout will contain, so the reader knows before they spend the paper. */
  tooltip: string;
  label?: string;
}) {
  const t = useT();
  return (
    <Tooltip relationship="description" content={tooltip}>
      <Button icon={<Print16Regular />} onClick={() => window.print()}>
        {label ?? t('common.action.print')}
      </Button>
    </Tooltip>
  );
}
