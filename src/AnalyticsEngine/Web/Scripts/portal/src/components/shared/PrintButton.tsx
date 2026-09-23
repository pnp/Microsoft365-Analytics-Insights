import { useCallback, useEffect, useState } from 'react';
import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  DialogTrigger,
  Spinner,
  Tooltip,
} from '@fluentui/react-components';
import { Print16Regular } from '@fluentui/react-icons';
import { useT } from '../../i18n';
import { formatCount } from './KpiGrid';
import { requestPrint, usePrintPhase, type PrintOutcome } from './printPreparation';

type Refusal = Extract<PrintOutcome, { kind: 'tooManyRows' } | { kind: 'failed' }>;

/**
 * Prints the report currently on screen, without the app shell around it.
 *
 * The layout is done by the `@media print` block in index.css, which hides everything marked
 * `data-print="hide"` (the brand bar, the area switcher, the nav rail, the page's own filter
 * controls and this button) and flattens the `data-print="content"` wrappers so the report gets the
 * full width of the sheet.
 *
 * What the stylesheet cannot do is put rows on the page that are not there. A paged list holds only
 * the page on screen, so this button goes through `requestPrint`, which loads every row of such a
 * list first - and refuses, with an explanation, when a list is too long to print, rather than
 * printing its first page as though it were the whole thing. Ctrl+P is routed through the same path
 * while the button is on the page, so the keyboard and the button produce the same printout.
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
  const phase = usePrintPhase();
  const [refusal, setRefusal] = useState<Refusal | null>(null);

  const print = useCallback(() => {
    void requestPrint().then((outcome) => {
      if (outcome.kind === 'tooManyRows' || outcome.kind === 'failed') setRefusal(outcome);
    });
  }, []);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.defaultPrevented || event.altKey || event.shiftKey) return;
      if (!(event.ctrlKey || event.metaKey) || event.key.toLowerCase() !== 'p') return;
      // The browser's own Ctrl+P would print the page as it stands - one page of each list.
      event.preventDefault();
      print();
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [print]);

  const preparing = phase === 'preparing';

  return (
    <>
      <Tooltip relationship="description" content={tooltip}>
        <Button
          icon={preparing ? <Spinner size="extra-tiny" /> : <Print16Regular />}
          disabledFocusable={preparing}
          onClick={print}
        >
          {preparing ? t('common.print.preparing') : (label ?? t('common.action.print'))}
        </Button>
      </Tooltip>

      <Dialog open={refusal !== null} onOpenChange={(_event, data) => !data.open && setRefusal(null)}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>
              {refusal?.kind === 'tooManyRows' ? t('common.print.tooManyRows.title') : t('common.print.failed.title')}
            </DialogTitle>
            <DialogContent>
              {refusal?.kind === 'tooManyRows'
                ? t('common.print.tooManyRows.body', {
                    rows: formatCount(refusal.rows),
                    limit: formatCount(refusal.limit),
                  })
                : t('common.print.failed.body')}
            </DialogContent>
            <DialogActions>
              <DialogTrigger disableButtonEnhancement>
                <Button appearance="primary">{t('common.action.close')}</Button>
              </DialogTrigger>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </>
  );
}
