import { buttonVariants } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import Image from "next/image";
import Link from "next/link";

export default function NotFound() {
  return (
    <main className="mt-[10%] flex h-full flex-col items-center p-4 pt-2">
      <Image src="/assets/404.svg" alt="Placeholder" width={300} height={300} />
      <h2 className="mt-4 text-lg font-extrabold text-slate-500">
        PAGE NOT FOUND
      </h2>
      <Link
        href="/"
        className={cn(
          buttonVariants({ variant: "outline", size: "sm" }),
          " mt-5"
        )}
      >
        Go back Home
      </Link>
    </main>
  );
}
