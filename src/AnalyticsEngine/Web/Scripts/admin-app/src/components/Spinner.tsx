type SpinnerProps = {
  /** Diameter in px. */
  size?: number;
  color?: string;
};

/**
 * Small dependency-free loading spinner. Replaces the old react-loader-spinner package
 * (whose API/CSS import path kept changing between majors).
 */
export default function Spinner({ size = 100, color = '#007bff' }: SpinnerProps) {
  const borderWidth = Math.max(2, Math.round(size / 12));
  return (
    <div
      className="aa-spinner"
      role="status"
      aria-label="Loading"
      style={{
        width: size,
        height: size,
        borderWidth,
        borderColor: `${color} ${color} transparent ${color}`,
      }}
    />
  );
}
