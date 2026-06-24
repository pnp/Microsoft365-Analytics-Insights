import { Spinner as FluentSpinner, type SpinnerProps } from '@fluentui/react-components';

type Props = {
  /** Approximate diameter in px (mapped to the nearest Fluent spinner size). */
  size?: number;
  label?: string;
};

function mapSize(px?: number): SpinnerProps['size'] {
  if (!px) return 'medium';
  if (px <= 16) return 'extra-tiny';
  if (px <= 24) return 'tiny';
  if (px <= 32) return 'small';
  if (px <= 64) return 'medium';
  if (px <= 96) return 'large';
  return 'huge';
}

/** Thin wrapper over the Fluent Spinner so existing call sites (which pass a px size) keep working. */
export default function Spinner({ size, label }: Props) {
  return <FluentSpinner size={mapSize(size)} label={label} />;
}
