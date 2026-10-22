// The copied data grid uses a few icons from HCM's icon library; Lucide equivalents stand in.
import {
  ChevronDown,
  ChevronUp,
  Eye,
  FileSpreadsheet,
  FileText,
  Pencil,
  Share,
  Table,
  Trash2,
  type LucideProps,
} from 'lucide-react';

const size = (props: LucideProps) => ({ width: 16, height: 16, ...props });

export const EyeIcon = (props: LucideProps) => <Eye {...size(props)} />;
export const PencilIcon = (props: LucideProps) => <Pencil {...size(props)} />;
export const TrashBinIcon = (props: LucideProps) => <Trash2 {...size(props)} />;
export const TableIcon = (props: LucideProps) => <Table {...size(props)} />;
export const AngleDownIcon = (props: LucideProps) => <ChevronDown {...size(props)} />;
export const AngleUpIcon = (props: LucideProps) => <ChevronUp {...size(props)} />;
export const ExportIcon = (props: LucideProps) => <Share {...size(props)} />;
export const PdfIcon = (props: LucideProps) => <FileText {...size(props)} />;
export const MicrosoftExcelIcon = (props: LucideProps) => <FileSpreadsheet {...size(props)} />;