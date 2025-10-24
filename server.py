from flask import Flask, request, jsonify
from deepface import DeepFace
import os
import traceback

app = Flask(__name__)

# 작업자 얼굴 이미지가 저장된 데이터베이스 폴더 경로
DB_PATH = "./DB_Images"

@app.route('/verify', methods=['POST'])
def verify_face():
    if 'image' not in request.files:
        return jsonify({"error": "No image file provided"}), 400

    image_file = request.files['image']
    temp_path = "temp_image.jpg"
    image_file.save(temp_path)

    try:
        # ArcFace 모델을 사용하여 DB에서 가장 유사한 얼굴 찾기
        dfs = DeepFace.find(
            img_path=temp_path,
            db_path=DB_PATH,
            model_name="ArcFace",
            enforce_detection=False
        )

        if not dfs[0].empty:
            most_similar = dfs[0].iloc[0]
            identity_path = most_similar['identity']

            # --- 이 부분이 수정되었습니다 ---
            # 파일명에서 확장자를 제거하여 작업자 ID로 추출
            worker_id = os.path.splitext(os.path.basename(identity_path))[0]
            # ---------------------------

            print(f"✅ Match found. Extracted Worker ID: {worker_id}")

            return jsonify({"status": "success", "worker_id": worker_id})
        else:
            print("⚠️ No matching worker found in the database.")
            return jsonify({"status": "failed", "message": "No matching worker found in the database."})

    except Exception as e:
        print(f"🔴 An error occurred: {e}")
        print(traceback.format_exc())
        return jsonify({"status": "error", "message": str(e)}), 500
    finally:
        if os.path.exists(temp_path):
            os.remove(temp_path)

if __name__ == '__main__':
    app.run(host='0.0.0.0', port=5000)