"""Add device context length and multi-turn job messages.

Revision ID: 0004_context_messages
Revises: 0003_job_thinking
"""
from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa


revision: str = "0004_context_messages"
down_revision: Union[str, None] = "0003_job_thinking"
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.add_column("devices", sa.Column("context_length", sa.Integer(), nullable=True))
    op.add_column("jobs", sa.Column("messages", sa.JSON(), nullable=True))


def downgrade() -> None:
    op.drop_column("jobs", "messages")
    op.drop_column("devices", "context_length")
