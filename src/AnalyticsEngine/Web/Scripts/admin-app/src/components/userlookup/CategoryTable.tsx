import type { UserDataCategory } from '../../types/userData';
import CategoryRow from './CategoryRow';

type CategoryTableProps = {
  upn: string;
  categories: UserDataCategory[];
};

export default function CategoryTable({ upn, categories }: CategoryTableProps) {
  const total = categories.reduce((sum, c) => sum + c.count, 0);

  return (
    <div className="card">
      <div className="card-header">
        <strong>
          Data held ({total.toLocaleString()} records across {categories.length} categories)
        </strong>
      </div>
      <div className="card-body">
        <table className="table">
          <thead>
            <tr>
              <th>Category</th>
              <th style={{ textAlign: 'right', width: 140 }}>Records</th>
              <th style={{ width: 140 }} />
            </tr>
          </thead>
          <tbody>
            {categories.map((c) => (
              <CategoryRow key={c.key} upn={upn} category={c} />
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
