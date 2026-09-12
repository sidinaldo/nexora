import { NgTemplateOutlet } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Observable } from 'rxjs';
import { PipelinesServico } from '../../nucleo/servicos/pipelines.servico';
import { ToastServico } from '../../nucleo/toast/toast.servico';
import { PipelineDto } from '../../nucleo/modelos';
import { textoSobre } from '../../nucleo/cor';

/** OS FUNIS DA EMPRESA.
 *
 *  ===================== POR QUE ESTA TELA EXISTE =====================
 *  Até aqui a empresa tinha um funil só e "as etapas" eram configuração dele, em `/etapas`. Com
 *  vários, uma tela única de etapas teria que perguntar "de qual funil?" antes de mostrar
 *  qualquer coisa — então as etapas passaram a ser alcançadas POR AQUI, uma pipeline de cada vez.
 *  ===================================================================
 *
 *  ===================== O QUE NÃO ESTÁ AQUI =====================
 *  Reordenar os funis no menu. Com teto de 8 e ordem estável, arrastar itens de menu é interface
 *  para um problema que ninguém tem ainda — e cada gesto de arrasto custa um teste que ninguém
 *  vai olhar. Entra quando alguém pedir.
 *  ============================================================== */
@Component({
  selector: 'app-pipelines',
  // `NgTemplateOutlet` porque criar e editar usam o MESMO formulário, num `<ng-template>`.
  // Sem o import o Angular só avisa no build e não renderiza nada — silencioso na tela.
  imports: [FormsModule, RouterLink, NgTemplateOutlet],
  templateUrl: './pipelines.html',
  styleUrl: './pipelines.css'
})
export class Pipelines implements OnInit {
  private servico = inject(PipelinesServico);
  private toast = inject(ToastServico);

  /** Espelha `ServicoPipelines.MaximoPipelines`. Duplicado para a tela esconder o formulário
   *  ANTES de o dono digitar um nome e levar 409. O servidor continua decidindo. */
  readonly maximo = 8;

  lista = this.servico.lista;
  carregando = signal(true);
  erro = signal('');
  salvando = signal(false);

  editando = signal<number | 'novo' | null>(null);
  fNome = signal('');
  fCor = signal('#5C8F6E');
  erroForm = signal('');

  removendo = signal<PipelineDto | null>(null);

  textoSobre = textoSobre;

  cheio = computed(() => this.lista().length >= this.maximo);

  /** ⚠️ A ÚLTIMA NÃO PODE SER APAGADA, e a API já recusa (ela é sempre a padrão). A tela checa
   *  também para o botão nem aparecer — descobrir a regra levando erro depois do clique é o que
   *  este projeto evita em toda tela de configuração. */
  podeApagar(p: PipelineDto) { return !p.padrao && this.lista().length > 1; }

  ngOnInit() { this.carregar(); }

  carregar() {
    this.carregando.set(true);
    this.servico.carregar().subscribe({
      next: () => { this.carregando.set(false); this.erro.set(''); },
      error: () => {
        this.erro.set('Não foi possível carregar as pipelines.');
        this.carregando.set(false);
      }
    });
  }

  // ---------------------------------------------------------------- formulário
  abrirNovo() {
    this.editando.set('novo');
    this.fNome.set('');
    this.fCor.set('#5C8F6E');
    this.erroForm.set('');
  }

  editar(p: PipelineDto) {
    this.editando.set(p.id);
    this.fNome.set(p.nome);
    this.fCor.set(p.cor);
    this.erroForm.set('');
  }

  fechar() { this.editando.set(null); this.erroForm.set(''); }

  salvar() {
    const alvo = this.editando();
    const nome = this.fNome().trim();
    if (alvo === null || this.salvando()) return;
    if (nome.length < 2) { this.erroForm.set('Dê um nome à pipeline.'); return; }

    this.salvando.set(true);
    this.erroForm.set('');

    // `Observable<unknown>`: criar devolve `{ id }` e atualizar devolve `void`, e a união das
    // duas assinaturas não é chamável.
    const requisicao: Observable<unknown> = alvo === 'novo'
      ? this.servico.criar(nome, this.fCor())
      : this.servico.atualizar(alvo, nome, this.fCor());

    requisicao.subscribe({
      next: () => {
        this.salvando.set(false);
        this.toast.sucesso(alvo === 'novo' ? `Pipeline "${nome}" criada.` : `Pipeline "${nome}" salva.`);
        this.editando.set(null);
        this.carregar();
      },
      // A mensagem do servidor é a que importa: ela distingue "já existe" de "chegou no limite".
      // O formulário fica aberto com o que foi digitado.
      error: e => {
        this.salvando.set(false);
        this.erroForm.set(e.error?.erro ?? 'Não foi possível salvar.');
      }
    });
  }

  // ---------------------------------------------------------------- padrão
  definirPadrao(p: PipelineDto) {
    if (this.salvando()) return;
    this.salvando.set(true);

    this.servico.definirPadrao(p.id).subscribe({
      next: () => {
        this.salvando.set(false);
        this.toast.sucesso(`Lead novo passa a entrar em "${p.nome}".`);
        this.carregar();
      },
      error: e => {
        this.salvando.set(false);
        this.toast.erro(e.error?.erro ?? 'Não foi possível mudar o padrão.');
      }
    });
  }

  // ---------------------------------------------------------------- remover
  confirmarRemocao(p: PipelineDto) { this.removendo.set(p); }
  cancelarRemocao() { this.removendo.set(null); }

  remover() {
    const alvo = this.removendo();
    if (!alvo || this.salvando()) return;

    this.salvando.set(true);
    this.servico.remover(alvo.id).subscribe({
      next: () => {
        this.salvando.set(false);
        this.removendo.set(null);
        this.toast.info(`Pipeline "${alvo.nome}" apagada.`);
        this.carregar();
      },
      // A API recusa quem tem contato nas etapas, e a mensagem dela diz QUANTOS. Reimplementar a
      // contagem aqui seria uma segunda cópia da regra, que divergiria.
      error: e => {
        this.salvando.set(false);
        this.removendo.set(null);
        this.toast.erro(e.error?.erro ?? 'Não foi possível apagar.');
      }
    });
  }

  aoTeclarNoModal(evento: KeyboardEvent) {
    if (evento.key === 'Escape') { evento.preventDefault(); this.cancelarRemocao(); }
  }
}
