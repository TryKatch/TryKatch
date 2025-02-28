"use client";
import { ReactElement, useEffect, useRef, useState } from "react";
import { ButtonUI } from "@/components/ui/button";
import { useProfileStore } from "@/hooks/zustand/users/useProfile";
import { Plus, Search } from "lucide-react";
import { useQueryString } from "@/hooks/useQueryString";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import Button from "@/components/custom/Button";

import { useGetList } from "@/hooks/api/common/getAll";

import { Bar } from "react-chartjs-2";
import {
  Chart as ChartJS,
  CategoryScale,
  LinearScale,
  BarElement,
  Title,
  Tooltip,
  Legend,
  ArcElement,
} from "chart.js";
import { boolean } from "zod";
import { DataList } from "@/components/custom/list/DataList";
import { getColumns } from "./table";
import { ExportModal } from "@/components/custom/common/export-data-modal";
import { IUnknown } from "@/interface/Iunknown";
import CreateExpenseModal from "./create-expense/addModal";
import DeleteEmployment from "./delete-expense/deleteExpense";
import UpdateExpense from "./edit-expense/updateExpense";
import dayjs from "dayjs";
import { useReactToPrint } from "react-to-print";
import { PrintExpensesReport } from "./print";
import ChartTooltip from "./tooltip";

// Register ChartJS components
ChartJS.register(
  CategoryScale,
  LinearScale,
  BarElement,
  Title,
  Tooltip,
  Legend,
  ArcElement
);

interface ExportOption {
  id: string;
  label: string;
  enabled: boolean;
  display: string;
}

const AccountDashBoard = () => {
  const [exportOpen, setExportOpen] = useState<boolean>(false);
  const [addOpen, setAddOpen] = useState<boolean>(false);
  const [removeOpen, setRemoveOpen] = useState<boolean>(false);
  const [editOpen, setEditOpen] = useState<boolean>(false);

  const contentRef = useRef<HTMLDivElement>(null);

  const [selectedExpense, setSelectedExpense] = useState<IUnknown>([]);
  const [filterDate, setFilterDate] = useState({ from: "", to: "" });

  const { getQueryObject, pushQuery } = useQueryString();

  const {
    data: expenses,
    isLoading: expenseLoading,
    refetch,
  } = useGetList({
    queryKey: "expenses",
    endpoint: "/expenses",
  });

  const { data: expenseCategories } = useGetList({
    queryKey: "expenses-categories",
    endpoint: "/expense-categories",
  });

  const isFiltered = dayjs(filterDate.from).isBefore(dayjs(filterDate.to));

  const { data: filteredExpense, isLoading } = useGetList({
    queryKey: "filtered-expenses",
    endpoint: "/filtered-expenses",
    filter: {
      startDate: filterDate.from,
      endDate: filterDate.to,
    },
    enabled: isFiltered,
  });

  const { data: userProfile } = useProfileStore();
  const user = userProfile?.user?.fname || "";

  const tableData = !isFiltered ? expenses : filteredExpense;

  type ReduceAccValue = {
    total: number;
    items: Array<{
      name: string;
      amount: string;
    }>;
  };
  const graphData: Record<string, ReduceAccValue> = (tableData || []).reduce(
    (acc: Record<string, ReduceAccValue>, curr: any) => {
      const date = dayjs(curr.created_at).format("YYYY-MM-DD");
      if (acc?.[date]) {
        acc[date] = {
          total: acc[date].total + Number(curr.amount),
          items: [
            ...acc[date].items,
            {
              name: curr?.category?.category,
              amount: curr.amount,
            },
          ],
        };
      } else {
        acc[date] = {
          total: Number(curr.amount),
          items: [
            {
              name: curr?.category?.category,
              amount: curr.amount,
            },
          ],
        };
      }
      return acc;
    },
    {}
  );

  console.log(graphData);
  const [tooltipData, setTooltipData] = useState<any>(null);
  const chartRef = useRef(null);

  // {
  //   "2025-12-01": 23,
  //   "2025-12-02": 233,
  // }

  const monthlyVisitsData = {
    // labels: graphicalData?.map((item: any) => item.category.category), // Use category names as labels
    labels: Object.keys(graphData), // Use category names and dates as labels
    datasets: [
      {
        label: "Expenses",
        data: Object.values(graphData).map((item) => item.total), // Use amounts from the data
        backgroundColor: "#2f78ee",
        barThickness: 16,
        borderRadius: 6,
      },
    ],
  };

  const chartOptions = {
    responsive: true,
    plugins: {
      legend: {
        position: "top" as const,
      },
      tooltip: {
        enabled: false, // Disable default tooltip
        external: (context: any) => {
          const chart = chartRef.current as any;
          if (!chart) return;

          const tooltipModel = context.tooltip;
          if (tooltipModel.opacity === 0) {
            setTooltipData(null);
            return;
          }

          const position = chart.canvas.getBoundingClientRect();
          const index = tooltipModel.dataPoints[0].label;
          setTooltipData({
            label: tooltipModel.dataPoints[0].label,
            total: graphData?.[index].total,
            items: graphData?.[index].items,
            position: {
              top: position.top + window.scrollY + tooltipModel.caretY,
              left: position.left + window.scrollX + tooltipModel.caretX,
            },
          });
        },
      },
    },
    scales: {
      x: {
        stacked: true,
      },
      y: {
        stacked: true,
      },
    },
  };

  const [options, setOptions] = useState<ExportOption[]>([
    {
      id: "date added",
      label: "date added",
      enabled: true,
      display: "Date added",
    },
    {
      id: "expense",
      label: "expense",
      enabled: true,
      display: "Expense",
    },
    {
      id: "amount",
      label: "amount",
      enabled: true,
      display: "Amount",
    },
    {
      id: "description",
      label: "description",
      enabled: true,
      display: "Description",
    },
  ]);

  const handleToggle = (id: string) => {
    setOptions(
      options.map((option) =>
        option.id === id ? { ...option, enabled: !option.enabled } : option
      )
    );
  };

  const dataToExport = tableData?.map((item: any) => ({
    "date added": item?.created_at,
    expense: item?.category?.category,
    amount: item?.amount,
    description: item?.description,
  }));

  const reactToPrintFn = useReactToPrint({
    contentRef,
    documentTitle: "expenses-report",
  });

  const handlePrint = () => {
    reactToPrintFn();
  };

  return (
    <div className="p-4">
      <div className="flex items-center justify-between my-4">
        <h1 className="text-2xl font-semibold">Hi {user} 👋</h1>
        <ButtonUI
          variant="default"
          type="button"
          onClick={() => setAddOpen(true)}
        >
          <Plus />
          Add expense
        </ButtonUI>
      </div>
      <div className="my-6">
        <PrintExpensesReport
          expenses={(tableData || []) as any[]}
          from={filterDate.from}
          to={filterDate.to}
          contentRef={contentRef}
        />
        <Card>
          <CardHeader className="flex flex-row item-center justify-between">
            <CardTitle>Expenses</CardTitle>
            <div className="flex items-center gap-5">
              <input
                type="date"
                onChange={(e) =>
                  setFilterDate({
                    ...filterDate,
                    from: e.target.value,
                  })
                }
              />
              <input
                type="date"
                onChange={(e) =>
                  setFilterDate({ ...filterDate, to: e.target.value })
                }
              />
            </div>
          </CardHeader>
          <CardContent>
            <div>
              <Bar
                ref={chartRef}
                options={chartOptions}
                data={monthlyVisitsData}
              />
              <ChartTooltip
                data={tooltipData}
                position={tooltipData?.position}
              />
            </div>
          </CardContent>
        </Card>
      </div>
      <div className="my-6">
        <Card>
          <CardHeader className="flex flex-row item-center justify-between">
            <CardTitle>Expenses</CardTitle>
            <div className="flex items-center gap-6">
              <div className="relative">
                <Input
                  type="text"
                  placeholder="Search expense"
                  className="w-full p-4 pr-10"
                  onChange={(e) => {
                    pushQuery("search", e.target.value);
                  }}
                />
                <Search className="h-4 w-4 text-muted-foreground absolute right-4 top-4" />
              </div>
              <div className="flex gap-3">
                <Button
                  className="p-6"
                  onClick={() => setExportOpen(true)}
                  icon="FileText"
                >
                  Export Data
                </Button>
              </div>
            </div>
          </CardHeader>
          <CardContent>
            <DataList
              columns={getColumns({
                onEdit(id, extraData) {
                  setSelectedExpense(extraData as IUnknown);
                  setEditOpen(true);
                },
                onDelete(id, extraData) {
                  // console.log(id, "=====>>>>>", extraData);
                  setSelectedExpense(extraData as IUnknown);
                  setRemoveOpen(true);
                },
              })}
              data={(tableData || []) as any[]}
              state={{ loading: expenseLoading || isLoading }}
            />
            <ExportModal
              open={exportOpen}
              onOpenChange={setExportOpen}
              onHandleToggle={handleToggle}
              options={options}
              data={dataToExport}
              exportName="expenses"
              exportPdf={true}
              onExportToPDF={handlePrint}
            />
          </CardContent>
        </Card>
        {addOpen && (
          <CreateExpenseModal
            closeModal={() => setAddOpen(false)}
            refetch={refetch}
            categories={(expenseCategories || []) as any[]}
          />
        )}
        {removeOpen && (
          <DeleteEmployment
            onClose={() => setRemoveOpen(false)}
            data={selectedExpense}
            refetch={refetch}
          />
        )}
        {editOpen && (
          <UpdateExpense
            categories={(expenseCategories || []) as any[]}
            closeModal={() => setEditOpen(false)}
            data={selectedExpense}
            refetch={refetch}
          />
        )}
      </div>
    </div>
  );
};

export default AccountDashBoard;
