"use client";

import { useEffect, useState } from "react";
import axios from "axios";
import Link from "next/link";

// type BlogPost = {
//   id: number;
//   attributes: {
//     title: string;
//     slug: string;
//     content: string;
//   };
// };

const BlogList = () => {
  const [posts, setPosts] = useState<any>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    const fetchPosts = async () => {
      try {
        const { data } = await axios.get(
          "http://localhost:1337/api/blog-posts"
        );
        setPosts(data.data);
      } catch (error) {
        console.error("Error fetching posts:", error);
      } finally {
        setLoading(false);
      }
    };

    fetchPosts();
  }, []);

  console.log(posts);
  if (loading) return <p>Loading...</p>;

  return (
    <div className="container mx-auto p-5">
      <h1 className="text-2xl font-bold">Blog</h1>
      {posts.map((post: any) => (
        <div key={post.id} className="mt-4">
          <h2 className="text-xl font-semibold">{post?.title}</h2>
          <Link href={`/blog/${post?.slug}`} className="text-blue-500">
            Read More
          </Link>
        </div>
      ))}
    </div>
  );
};

export default BlogList;
