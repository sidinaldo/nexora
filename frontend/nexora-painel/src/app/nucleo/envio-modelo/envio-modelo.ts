import { Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CaixaServico } from '../servicos/caixa.servico';
import { ToastServico } from '../toast/toast.servico';
import { ModeloDaConversa } from '../modelos';

/** O TEMPLATE, QUANDO A JANELA DE 24H FECHOU (INT-XX).
 *
 *  Na API oficial, depois de 24 horas sem o cliente escrever, só sai template aprovado pela Meta.
 *  Este pedaço fica no lugar do compositor: escolhe um, mostra o texto JÁ PREENCHIDO para este
 *  cliente — o que ele vai ler — e envia.
 *
 *  A lista e o texto vêm do servidor. A tela não preenche variável nenhuma: o que aparece aqui é o
 *  mesmo texto que fica gravado na thread. */
@Component({
  selector: 'app-envio-modelo',
  imports: [FormsModule],
  templateUrl: './envio-modelo.html',
  styleUrl: './envio-modelo.css'
})
export class EnvioModelo {
  private servico = inject(CaixaServico);
  private toast = inject(ToastServico);

  conversaId = input.required<number>();

  /** Saiu — ou ficou gravado como "não chegou". Nos dois casos a thread recarrega. */
  enviado = output<void>();

  modelos = signal<ModeloDaConversa[]>([]);
  carregando = signal(true);
  escolhidoId = signal<number | null>(null);
  escolhido = computed(() => this.modelos().find(m => m.id === this.escolhidoId()) ?? null);
  enviando = signal(false);

  constructor() {
    effect(() => {
      const id = this.conversaId();
      untracked(() => this.carregar(id));
    });
  }

  private carregar(conversaId: number) {
    this.carregando.set(true);
    this.escolhidoId.set(null);
    this.servico.modelos(conversaId).subscribe({
      next: l => {
        this.modelos.set(l);
        this.carregando.set(false);
        // Um só: já escolhido. Obrigar a escolher a única opção é um clique sem decisão.
        if (l.length === 1) this.escolhidoId.set(l[0].id);
      },
      error: () => {
        this.modelos.set([]);
        this.carregando.set(false);
      }
    });
  }

  enviar() {
    const m = this.escolhido();
    if (m === null) return;

    this.enviando.set(true);
    this.servico.enviarModelo(this.conversaId(), m.id).subscribe({
      next: r => {
        this.enviando.set(false);
        // Como a resposta escrita: a linha existe e aparece como "não chegou", com o motivo.
        if (!r.enviada) this.toast.erro(r.erro ?? 'O template foi registrado mas não chegou ao WhatsApp.');
        this.enviado.emit();
      },
      error: e => {
        this.enviando.set(false);
        this.toast.erro(e.error?.erro ?? 'Não foi possível enviar o template.');
      }
    });
  }
}
