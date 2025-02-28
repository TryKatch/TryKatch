"use client";

import { useEffect, useState } from "react";
import { useParams } from "next/navigation";
import axios from "axios";

import RichTextRenderer from "@/app/common/richTextRender";

const BlogDetail = () => {
  const { slug } = useParams();
  const [post, setPost] = useState<any>(null);
  const [loading, setLoading] = useState(true);

  console.log(slug);

  useEffect(() => {
    if (!slug) return;

    const fetchPost = async () => {
      try {
        const { data } = await axios.get(
          `http://localhost:1337/api/blog-posts?filters[slug][$eq]=${slug}`
        );
        setPost(data.data[0]);
      } catch (error) {
        console.error("Error fetching post:", error);
      } finally {
        setLoading(false);
      }
    };

    fetchPost();
  }, [slug]);

  if (loading) return <p>Loading...</p>;
  if (!post) return <p>Post not found</p>;
  console.log(post);

  return (
    <div className="container mx-auto p-5">
      <h1 className="text-2xl font-bold">{post?.title}</h1>
      {/* <p className="mt-4">{post.content}</p> */}
      <RichTextRenderer content={post.content} />
    </div>
  );
};

export default BlogDetail;
