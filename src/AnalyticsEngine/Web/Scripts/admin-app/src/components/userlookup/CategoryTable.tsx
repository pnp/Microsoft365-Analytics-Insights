import {
  Card,
  CardHeader,
  Subtitle2,
  Text,
  Badge,
  Tooltip,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { CheckmarkCircle16Filled, Circle16Regular } from '@fluentui/react-icons';
import type { UserDataCategory, Workload } from '../../types/userData';
import CategoryRow from './CategoryRow';

const useStyles = makeStyles({
  cards: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
  },
  workloads: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: '8px',
    marginTop: '8px',
  },
  hint: {
    color: tokens.colorNeutralForeground3,
    marginTop: '8px',
  },
  list: {
    marginTop: '8px',
  },
});

type CategoryTableProps = {
  upn: string;
  categories: UserDataCategory[];
  workloads: Workload[];
};

export default function CategoryTable({ upn, categories, workloads }: CategoryTableProps) {
  const styles = useStyles();
  const total = categories.reduce((sum, c) => sum + c.count, 0);
  const enabledCount = workloads.filter((w) => w.enabled).length;

  return (
    <div className={styles.cards}>
      <Card>
        <CardHeader
          header={
            <Subtitle2>
              Import workloads ({enabledCount} of {workloads.length} enabled)
            </Subtitle2>
          }
        />
        <Text size={200} className={styles.hint}>
          Data is only collected for enabled workloads. A category fed only by disabled workloads will show 0 records -
          that is expected, not a fault.
        </Text>
        <div className={styles.workloads}>
          {workloads.map((w) => (
            <Tooltip key={w.name} relationship="description" content={w.description}>
              <Badge
                appearance={w.enabled ? 'filled' : 'outline'}
                color={w.enabled ? 'success' : 'informative'}
                icon={w.enabled ? <CheckmarkCircle16Filled /> : <Circle16Regular />}
              >
                {w.name}
              </Badge>
            </Tooltip>
          ))}
        </div>
      </Card>

      <Card>
        <CardHeader
          header={
            <Subtitle2>
              Data held ({total.toLocaleString()} records across {categories.length} categories)
            </Subtitle2>
          }
        />
        <Text size={200} className={styles.hint}>
          Hover the info icon on any row to see the SQL query behind the count (click it to copy).
        </Text>
        <div className={styles.list}>
          {categories.map((c) => (
            <CategoryRow key={c.key} upn={upn} category={c} />
          ))}
        </div>
      </Card>
    </div>
  );
}
