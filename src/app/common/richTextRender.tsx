"use client";

import { Prism as SyntaxHighlighter } from "react-syntax-highlighter";
import { atomDark } from "react-syntax-highlighter/dist/esm/styles/prism";

type RichTextNode = {
  type: string;
  children: {
    text: string;
    bold?: boolean;
    italic?: boolean;
    code?: boolean;
  }[];
  level?: number;
};

const RichTextRenderer = ({ content }: { content: RichTextNode[] }) => {
  return (
    <div className="prose max-w-none">
      {content.map((node, index) => {
        switch (node.type) {
          case "heading":
            const HeadingTag = `h${node.level}` as any;
            return (
              <HeadingTag key={index} className="font-bold">
                {node.children.map((child, i) => (
                  <span
                    key={i}
                    className={`${child.bold ? "font-bold" : ""} ${
                      child.italic ? "italic" : ""
                    }`}
                  >
                    {child.text}
                  </span>
                ))}
              </HeadingTag>
            );

          case "paragraph":
            return (
              <p key={index} className="mb-4">
                {node.children.map((child, i) =>
                  child.code ? (
                    <SyntaxHighlighter
                      key={i}
                      language="javascript"
                      style={atomDark}
                      customStyle={{ padding: "10px", borderRadius: "5px" }}
                    >
                      {child.text}
                    </SyntaxHighlighter>
                  ) : (
                    <span key={i}>{child.text}</span>
                  )
                )}
              </p>
            );

          default:
            return null;
        }
      })}
    </div>
  );
};

export default RichTextRenderer;
