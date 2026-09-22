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
import CategoryRow, { USER_DATA_WORKLOADS_BY_FLAG, userDataWorkloadName } from './CategoryRow';
import { formatNumber, useT, useTNode } from '../../i18n';

const USER_DATA_WORKLOAD_DESCRIPTION_BY_NAME = new Map(
  Object.values(USER_DATA_WORKLOADS_BY_FLAG).map((entry) => [entry.english, entry.descriptionKey]),
);

function userDataWorkloadDescription(t: ReturnType<typeof useT>, workload: Workload): string {
  const key = USER_DATA_WORKLOAD_DESCRIPTION_BY_NAME.get(workload.name);
  return key ? t(key) : workload.description;
}

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
  const t = useT();
  const tNode = useTNode();
  const total = categories.reduce((sum, c) => sum + c.count, 0);
  const enabledCount = workloads.filter((w) => w.enabled).length;

  return (
    <div className={styles.cards}>
      <Card>
        <CardHeader
          header={
            <Subtitle2>
              {t('admin.userLookup.categoryTable.importWorkloadsTitle', {
                enabledCount: formatNumber(enabledCount),
                totalCount: formatNumber(workloads.length),
              })}
            </Subtitle2>
          }
        />
        <Text size={200} className={styles.hint}>
          {t('admin.userLookup.categoryTable.importWorkloadsHint')}
        </Text>
        <div className={styles.workloads}>
          {workloads.map((w) => (
            <Tooltip
              key={w.name}
              relationship="description"
              content={userDataWorkloadDescription(t, w)}
            >
              <Badge
                appearance={w.enabled ? 'filled' : 'outline'}
                color={w.enabled ? 'success' : 'informative'}
                icon={w.enabled ? <CheckmarkCircle16Filled /> : <Circle16Regular />}
              >
                {userDataWorkloadName(t, w.name)}
              </Badge>
            </Tooltip>
          ))}
        </div>
      </Card>

      <Card>
        <CardHeader
          header={
            <Subtitle2>
              {t('admin.userLookup.categoryTable.dataHeldTitle', {
                records: formatNumber(total),
                categories: formatNumber(categories.length),
              })}
            </Subtitle2>
          }
        />
        <Text size={200} className={styles.hint}>
          {tNode('admin.userLookup.categoryTable.sqlHint', { sql: <strong>SQL</strong> })}
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
