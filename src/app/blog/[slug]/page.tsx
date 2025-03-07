"use client";

// import { SiteFooter } from "@/app/common/footer";
// import BlogDetail from "./blogDetail";
// import { NavBar } from "@/app/common/navBar";

// export default function BlogDetailPage() {
//   return (
//     <div>
//       <NavBar />
//       <BlogDetail />
//       <SiteFooter />
//     </div>
//   );
// }

import Link from "next/link";
import Image from "next/image";
import { Badge } from "@/components/ui/badge";
import { Avatar, AvatarFallback, AvatarImage } from "@/components/ui/avatar";
import { Button } from "@/components/ui/button";
import { ArrowLeft } from "lucide-react";
import { useParams } from "next/navigation";

// Types for our blog data
interface Author {
  name: string;
  role: string;
  avatar: string;
}

interface BlogPost {
  id: string;
  title: string;
  description: string;
  content: string;
  image: string;
  date: string;
  readTime: string;
  category: string;
  author: Author;
}

// This would typically come from a database or CMS
const getBlogPost = (slug: string): BlogPost | undefined => {
  const blogPosts: Record<string, BlogPost> = {
    "webinar-riverside": {
      id: "webinar-riverside",
      title:
        "How to Run a Webinar with Riverside: A Complete Step-by-Step Guide",
      description:
        "This complete guide shows you how you can plan, schedule, host, recording, and repurpose your webinar with just one platform; Riverside!",
      content: `
        <p>Running a successful webinar requires careful planning, the right tools, and a solid strategy. In this comprehensive guide, we'll walk you through everything you need to know about hosting webinars using Riverside.</p>
        
        <h2>Why Choose Riverside for Your Webinars?</h2>
        <p>Riverside offers a complete solution for webinar hosts, combining high-quality recording, easy scheduling, and powerful repurposing tools all in one platform.</p>
        
        <h2>Step 1: Planning Your Webinar</h2>
        <p>Before you dive into the technical setup, take time to plan your webinar content, define your audience, and set clear objectives for what you want to achieve.</p>
        
        <h2>Step 2: Setting Up Your Riverside Account</h2>
        <p>Create your Riverside account and familiarize yourself with the dashboard. This is where you'll manage all aspects of your webinar.</p>
        
        <h2>Step 3: Scheduling Your Webinar</h2>
        <p>Use Riverside's scheduling features to set a date and time for your webinar. You can send automated invitations to participants and set reminders.</p>
        
        <h2>Step 4: Preparing Your Equipment</h2>
        <p>Ensure you have a good quality microphone, camera, and stable internet connection. Test your setup before the webinar day.</p>
        
        <h2>Step 5: Running Your Webinar</h2>
        <p>On the day of your webinar, log in early, check your equipment again, and follow your content plan. Riverside makes it easy to manage participants and share your screen.</p>
        
        <h2>Step 6: Repurposing Your Content</h2>
        <p>After your webinar, use Riverside's editing tools to create clips, highlights, and other content pieces from your recording.</p>
        
        <h2>Conclusion</h2>
        <p>With Riverside, running webinars becomes a streamlined process from planning to repurposing. Follow this guide to ensure your next webinar is a success!</p>
      `,
      image: "/placeholder.png",
      date: "January 23, 2025",
      readTime: "20 min",
      category: "Webinar",
      author: {
        name: "Kendall Breitman",
        role: "Social Media & Community Expert",
        avatar: "/placeholder.png",
      },
    },
    "video-podcast-2025": {
      id: "video-podcast-2025",
      title: "How to Record a Video Podcast in 2025 (5 Easy Methods)",
      description:
        "Learn the best ways to record high-quality video podcasts in 2025",
      content: `
        <p>Video podcasting has become increasingly popular, and in 2025, there are more options than ever for creating professional-quality content. This guide explores five easy methods to record your video podcast.</p>
        
        <h2>Method 1: All-in-One Podcast Platforms</h2>
        <p>Platforms like Riverside and Zencastr offer comprehensive solutions for recording both audio and video simultaneously with remote guests.</p>
        
        <h2>Method 2: Video Conferencing Tools</h2>
        <p>Tools like Zoom and Microsoft Teams can be used to record video podcasts, though they typically offer lower quality than dedicated podcast platforms.</p>
        
        <h2>Method 3: Local Recording Setup</h2>
        <p>For solo podcasters or co-located teams, setting up a local recording studio with cameras, microphones, and recording software provides maximum quality control.</p>
        
        <h2>Method 4: Mobile Recording</h2>
        <p>Modern smartphones can capture surprisingly high-quality video and audio, making them a viable option for podcasters on the go.</p>
        
        <h2>Method 5: Hybrid Approaches</h2>
        <p>Combining multiple recording methods can provide backup options and flexibility for different podcast formats and guest situations.</p>
        
        <h2>Conclusion</h2>
        <p>Choose the method that best fits your technical comfort level, budget, and podcast format. Each approach has its strengths and can produce professional results when used correctly.</p>
      `,
      image: "/placeholder.png",
      date: "Jan 25, 2025",
      readTime: "14 min",
      category: "Video podcast",
      author: {
        name: "Stephen Robles",
        role: "Video & Podcast Creator",
        avatar: "/placeholder.png",
      },
    },
    // Add more blog posts as needed
  };

  return blogPosts[slug];
};

export default function BlogPostPage() {
  // const post = getBlogPost(params?.slug);
  const { slug } = useParams(); // Get slug from URL
  const post = getBlogPost(slug as string); // Fetch the blog post

  if (!post) {
    return (
      <div className="container mx-auto px-4 py-16 text-center">
        <h1 className="text-3xl font-bold mb-4">Blog Post Not Found</h1>
        <p className="mb-8">
          The blog post you&apos;re looking for doesn&apos;t exist or has been
          removed.
        </p>
        <Button asChild>
          <Link href="/blog">
            <ArrowLeft className="mr-2 h-4 w-4" />
            Back to Blog
          </Link>
        </Button>
      </div>
    );
  }

  return (
    <div className="container mx-auto px-4 py-8 max-w-4xl">
      <div className="mb-8">
        <Button variant="ghost" asChild className="mb-4">
          <Link href="/blog">
            <ArrowLeft className="mr-2 h-4 w-4" />
            Back to Blog
          </Link>
        </Button>

        <Badge className="mb-4">{post.category}</Badge>
        <h1 className="text-4xl font-bold mb-4">{post.title}</h1>
        <p className="text-xl text-gray-600 mb-6">{post.description}</p>

        <div className="flex items-center gap-4 mb-8">
          <Avatar className="h-12 w-12">
            <AvatarImage src={post.author.avatar} alt={post.author.name} />
            <AvatarFallback>{post.author.name.charAt(0)}</AvatarFallback>
          </Avatar>
          <div>
            <p className="font-medium">{post.author.name}</p>
            <p className="text-sm text-gray-500">{post.author.role}</p>
          </div>
          <div className="text-sm text-gray-500 ml-auto">
            {post.date} • {post.readTime}
          </div>
        </div>

        <Image
          src={post.image || "/placeholder.svg"}
          alt={post.title}
          width={1200}
          height={600}
          className="rounded-lg object-cover w-full h-[300px] md:h-[500px] mb-8"
        />

        <div
          className="prose prose-lg max-w-none"
          dangerouslySetInnerHTML={{ __html: post.content }}
        />
      </div>

      <div className="border-t pt-8 mt-12">
        <h2 className="text-2xl font-bold mb-4">Share this article</h2>
        <div className="flex gap-2">
          <Button variant="outline">Twitter</Button>
          <Button variant="outline">Facebook</Button>
          <Button variant="outline">LinkedIn</Button>
          <Button variant="outline">Copy Link</Button>
        </div>
      </div>
    </div>
  );
}
